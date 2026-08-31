using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Remold.Core.Mesh;
using Remold.Core.Project;

namespace Remold.Core.Blender;

/// <summary>An edit handed back from Blender: the mesh in canonical Unity space, or null when the send
/// carried no mesh (a session whose only part was emptied). <see cref="NodeTransformIgnored"/>: the node
/// had a non-identity Object-mode transform the geometry-only import does NOT apply.
/// <see cref="HiddenParts"/> is the ONLY signal that hides a part — a glb carries one session's parts,
/// so a part's absence can never mean intent.
/// <see cref="EditIds"/> carries the per-part destination selected in Blender. A string names an existing
/// edit; a <c>{"new":"..."}</c> object requests a new edit with that name. A part it does not name keeps
/// the legacy return behavior.</summary>
public readonly record struct IncomingEdit(UnityMesh? Mesh, string GlbPath, bool NodeTransformIgnored = false,
    IReadOnlyList<string>? HiddenParts = null,
    IReadOnlyDictionary<string, BlenderPartTarget>? EditIds = null)
{
    public string Name => Mesh?.Name ?? "";

    /// <summary>The target this send selects for one part, or null where it selects none.</summary>
    public BlenderPartTarget? TargetFor(string part) =>
        part is not null && EditIds is { } ids && ids.TryGetValue(part, out var target) ? target : null;

    /// <summary>The existing edit this send selects for one part. Null for an absent or new-edit target.
    /// Kept as the compatibility view used by the pre-target-selection intake.</summary>
    public string? EditIdFor(string part) =>
        TargetFor(part)?.ExistingEditId;
}

/// <summary>A content edit offered as a Blender send destination for one writable session part.</summary>
public sealed record BlenderSessionEdit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("holdsAuthoredMesh")] bool HoldsAuthoredMesh);

/// <summary>The destination a part selected in Blender. Its JSON is deliberately a union under the existing
/// <c>editIds</c> sidecar key: an existing edit is its id string, while a new edit is
/// <c>{"new":"name"}</c>. A blank new name remains a valid request; intake resolves it through the app's
/// default-name policy at commit.</summary>
[JsonConverter(typeof(BlenderPartTarget.TargetJsonConverter))]
public sealed record BlenderPartTarget
{
    private BlenderPartTarget(string? existingEditId, string? newEditName)
    {
        ExistingEditId = existingEditId;
        NewEditName = newEditName;
    }

    [JsonIgnore] public string? ExistingEditId { get; }
    [JsonIgnore] public string? NewEditName { get; }
    [JsonIgnore] public bool IsExisting => ExistingEditId is not null;
    [JsonIgnore] public bool IsNew => NewEditName is not null;

    public static BlenderPartTarget Existing(string editId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editId);
        return new BlenderPartTarget(editId, null);
    }

    public static BlenderPartTarget New(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new BlenderPartTarget(null, name);
    }

    public sealed class TargetJsonConverter : JsonConverter<BlenderPartTarget>
    {
        public override BlenderPartTarget Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? editId = reader.GetString();
                if (string.IsNullOrWhiteSpace(editId)) throw new JsonException("An edit target id is empty.");
                return Existing(editId);
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("An edit target must be an edit id or a new-edit object.");
            using var doc = JsonDocument.ParseValue(ref reader);
            if (!doc.RootElement.TryGetProperty("new", out var name)
                || name.ValueKind != JsonValueKind.String)
                throw new JsonException("A new-edit target must carry a name.");
            return New(name.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, BlenderPartTarget value,
            JsonSerializerOptions options)
        {
            if (value.IsExisting)
            {
                writer.WriteStringValue(value.ExistingEditId);
                return;
            }
            if (!value.IsNew) throw new JsonException("An edit target has no destination.");
            writer.WriteStartObject();
            writer.WriteString("new", value.NewEditName);
            writer.WriteEndObject();
        }
    }
}

/// <summary>One part of a Blender session: the mesh name the glb carries it under, whether the app already
/// holds an edit for it (so Send can confirm what it would replace), and whether this session may write it
/// back at all. <see cref="Writable"/> false is how the app declares a part the session carries for CONTEXT
/// only — the bridge gives it the Reference collection, so it can be seen and never sent.
/// <see cref="Unskinned"/> declares a part whose mesh carries no skin (a static-renderer prop): the bridge
/// exempts it from the weight gate, which would otherwise count every one of its vertices as unweighted and
/// block the send. The app is the authority — "no armature in the scene" cannot tell an unskinned part from
/// a skinned one whose armature was deleted, and the second must still block.
///
/// <para><see cref="EditId"/> names the edit this part was opened from. <see cref="Edits"/> is the content
/// edit inventory the Blender panel may target, and <see cref="DefaultEditName"/> is the app's proposed
/// name when it targets a new edit. <see cref="ViewportVisible"/> is presentation metadata only and never a
/// return/hide signal. <see cref="Label"/> is the part's own short token as the app names it
/// (<c>cloth2</c>, <c>P3_body_fight</c>) — what the bridge panel and its messages call the part, so
/// Blender never re-derives a display name from the asset name's structure.</para></summary>
public readonly record struct SessionPart(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("edited")] bool Edited,
    [property: JsonPropertyName("writable")] bool? Writable = true,
    [property: JsonPropertyName("unskinned")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Unskinned = false,
    [property: JsonPropertyName("editId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EditId = null,
    [property: JsonPropertyName("edits")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BlenderSessionEdit>? Edits = null,
    [property: JsonPropertyName("defaultEditName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DefaultEditName = null,
    [property: JsonPropertyName("viewportVisible")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ViewportVisible = null,
    [property: JsonPropertyName("label")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Label = null)
{
    /// <summary>Whether the session may write this part back. Only an EXPLICIT false declares a part context;
    /// an absent flag is a session that said nothing about it, which reads the same as writable — matching the
    /// bridge, and keeping a sidecar the app didn't stamp from silently turning the whole outfit into
    /// scenery.</summary>
    [JsonIgnore] public bool IsWritable => Writable != false;

    /// <summary>The semantically named view of the legacy <c>editId</c> wire property.</summary>
    [JsonIgnore] public string? OpenedFromEditId => EditId;

    /// <summary>Old sessions carry no visibility field and remain visible.</summary>
    [JsonIgnore] public bool IsViewportVisible => ViewportVisible != false;
}

/// <summary>The app-owned session document beside the opened glb. <see cref="Revision"/> advances only when
/// this run is rewritten after an acknowledged intake, allowing a live Blender scene to distinguish its
/// launch snapshot from an app-committed contract.</summary>
public sealed record BlenderSessionDocument
{
    [JsonPropertyName("part")] public string? Part { get; init; }
    [JsonPropertyName("parts")] public List<SessionPart> Parts { get; init; } = new();
    [JsonPropertyName("sendAs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SendAs { get; init; }
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("notices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Notices { get; init; }
}

/// <summary>The exact binding a Blender material channel saw when the transport opened. This is a stale-write
/// guard, not an ownership lookup: the parent target supplies the edit id and this record supplies the exact
/// output slot plus the complete binding identity that may still be replaced on return.</summary>
public sealed record BlenderSlotBaseline(
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("submeshIndex")] int SubmeshIndex,
    [property: JsonPropertyName("input")] TargetInputKind Input,
    [property: JsonPropertyName("bindingKind")] BindingKind BindingKind,
    [property: JsonPropertyName("projectAssetId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProjectAssetId = null,
    [property: JsonPropertyName("sourceSlotId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceSlotId = null,
    [property: JsonPropertyName("sourceEditDefinitionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceEditDefinitionId = null,
    [property: JsonPropertyName("shaderProperty")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ShaderProperty = null);

/// <summary>One canonical destination named by an app-created Blender session. The return path is only an
/// ingress artifact; this record addresses the project asset and workspace revision it may update.
///
/// <para>Two compatible route shapes. An EXACT-SLOT row (<see cref="IsExactSlot"/>) carries the ingress an
/// older/target-less sidecar resumes. A PART row (<see cref="IsPartRoute"/>) carries the subject identity a
/// send-side target needs to address an edit at intake. New app sessions write both shapes on an opened-edit
/// row; older exact rows and older subject-only rows remain readable.</para></summary>
public sealed record BlenderSessionTarget(
    [property: JsonPropertyName("part")] string Part,
    [property: JsonPropertyName("projectAssetId")] string ProjectAssetId,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("editDefinitionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EditDefinitionId = null,
    [property: JsonPropertyName("slotId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SlotId = null,
    [property: JsonPropertyName("ingressReturn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IngressReturn = null,
    [property: JsonPropertyName("sourceBindingKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BindingKind? SourceBindingKind = null,
    [property: JsonPropertyName("materialSlots")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BlenderSlotBaseline>? MaterialSlots = null,
    [property: JsonPropertyName("subject")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Subject = null,
    [property: JsonPropertyName("outfit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outfit = null)
{
    [JsonIgnore] public bool IsExactSlot => !string.IsNullOrWhiteSpace(EditDefinitionId)
        && !string.IsNullOrWhiteSpace(SlotId) && IngressReturn is not null
        && Path.IsPathFullyQualified(IngressReturn);

    [JsonIgnore] public bool IsPartRoute => !string.IsNullOrWhiteSpace(Subject)
        && !string.IsNullOrWhiteSpace(Outfit) && Path.IsPathFullyQualified(Workspace);
}

/// <summary>One target row to promote when an intake is acknowledged. <see cref="PreparedWorkspace"/>
/// is a self-contained, disposable preparation; acknowledgement copies its directory into this run's
/// immutable baseline and writes that final path into <see cref="Target"/>.</summary>
public sealed record BlenderTargetAcknowledgement(BlenderSessionTarget Target, string PreparedWorkspace);

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
    private const string PartSendSuffix = ".send.glb";

    private sealed class SendDoc
    {
        [JsonPropertyName("hiddenParts")] public List<string> HiddenParts { get; set; } = new();
        [JsonPropertyName("editIds")] public Dictionary<string, BlenderPartTarget>? EditIds { get; set; }
    }

    private sealed class TargetDoc
    {
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
        /// <summary>The immutable glb against which the next return is compared. Initially this is the glb
        /// the launch handed Blender; every accepted intake advances it to a revisioned copy of that raw
        /// return. Written RELATIVE to this document's own folder and re-rooted there when read, so a mod
        /// folder rename does not strand a send that is still open in Blender. Absent on a document an older
        /// build wrote; an absolute path is read as given.</summary>
        [JsonPropertyName("openedGlb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OpenedGlb { get; set; }
        /// <summary>The glb whose adjacent session document owns this run. Initially the same as
        /// <see cref="OpenedGlb"/>, then deliberately stable while acknowledgements advance the comparison
        /// baseline. Absent in older target documents, where <see cref="OpenedGlb"/> remains the fallback.</summary>
        [JsonPropertyName("sessionGlb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SessionGlb { get; set; }
        [JsonPropertyName("targets")] public List<BlenderSessionTarget> Targets { get; set; } = new();
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
        return Process.Start(psi) ?? throw new InvalidOperationException("Blender did not start");
    }

    /// <summary>Where the session description lives: beside the glb, same stem-plus-suffix rule as the
    /// send sidecar, so the bridge derives it from the glb rather than the command line.</summary>
    public static string SessionPath(string glbPath) =>
        Path.Combine(Path.GetDirectoryName(glbPath) ?? "",
                     Path.GetFileNameWithoutExtension(glbPath) + SessionSuffix);

    /// <summary>The distinct return artifact for a lone-part session. Blender must never export directly
    /// over the workspace glb: its raw round-trip can carry connector joints that are useful scene nodes but
    /// are not game bones. The receive validates and normalizes this artifact before publishing anything to
    /// the workspace path.</summary>
    public static string PartSendPath(string workspaceGlb) =>
        Path.Combine(Path.GetDirectoryName(workspaceGlb) ?? "",
                     Path.GetFileNameWithoutExtension(workspaceGlb) + PartSendSuffix);

    /// <summary>The filename written into a lone-part session's <c>sendAs</c>.</summary>
    public static string PartSendName(string workspaceGlb) => Path.GetFileName(PartSendPath(workspaceGlb));

    public static string TargetPath(string returnGlb) => returnGlb + ".gf2target.json";

    /// <summary>Whether a return claims an app-created identity address. A present but malformed document
    /// must be refused rather than silently falling back to filename inference.</summary>
    public static bool ReturnTargetMetadataExists(string returnGlb) => File.Exists(TargetPath(returnGlb));

    /// <summary>Map a lone-part return artifact back to the workspace filename it was derived from. Null
    /// means the path does not carry the app's part-send suffix.</summary>
    public static string? WorkspaceForPartSend(string sendGlb)
    {
        var addressed = ReadReturnTargets(sendGlb);
        if (addressed.Count == 1) return addressed[0].Workspace;
        if (ReturnTargetMetadataExists(sendGlb)) return null;
        var name = Path.GetFileName(sendGlb);
        if (!name.EndsWith(PartSendSuffix, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = name[..^PartSendSuffix.Length];
        return Path.Combine(Path.GetDirectoryName(sendGlb) ?? "", stem + ".glb");
    }

    /// <summary>Describe the session: which mesh of the multi-part glb it may write back
    /// (<paramref name="sessionPart"/>; null = all), and every part in the glb with its edited flag.
    /// Must be written before the launch so the bridge's deferred import finds it.
    /// <paramref name="sendAs"/> names the file the bridge's Send exports as, in place of the opened glb's
    /// own name. App-created combined and lone-part sessions both name a distinct return artifact, keeping
    /// Blender's raw output off canonical workspace files. Null keeps the opened name for backward
    /// compatibility with hand-written/older session files.</summary>
    public static void WriteSession(string glbPath, string? sessionPart, IReadOnlyList<SessionPart> parts,
        string? sendAs = null, IReadOnlyList<BlenderSessionTarget>? targets = null,
        IReadOnlyList<string>? notices = null)
    {
        var doc = new BlenderSessionDocument
        {
            Part = sessionPart,
            Parts = new List<SessionPart>(parts),
            SendAs = sendAs,
            Revision = 1,
            Notices = notices is { Count: > 0 } ? new List<string>(notices) : null,
        };
        WriteSessionDocument(SessionPath(glbPath), doc);
        if (sendAs is null || targets is not { Count: > 0 }) return;
        string returned = Path.Combine(Path.GetDirectoryName(glbPath) ?? "", sendAs);
        var target = new TargetDoc
        {
            SessionId = Guid.NewGuid().ToString("N"),
            OpenedGlb = Path.GetRelativePath(TargetDocDirectory(returned), Path.GetFullPath(glbPath)),
            SessionGlb = Path.GetRelativePath(TargetDocDirectory(returned), Path.GetFullPath(glbPath)),
            Targets = targets.Select(t => t with
            {
                Workspace = Path.GetFullPath(t.Workspace),
                IngressReturn = t.IngressReturn is null ? null : Path.GetFullPath(t.IngressReturn),
            }).ToList(),
        };
        WriteTargetDocument(TargetPath(returned), target);
    }

    /// <summary>Read the complete live session contract. Null means it is absent or currently unreadable;
    /// a Blender caller can retain its scene snapshot and try this helper again after the app's next atomic
    /// rewrite.</summary>
    public static BlenderSessionDocument? ReadSessionDocument(string glbPath)
    {
        try
        {
            var path = SessionPath(glbPath);
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<BlenderSessionDocument>(File.ReadAllText(path), SessionJson);
            return doc is null ? null : doc with { Parts = doc.Parts ?? new List<SessionPart>() };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                  or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Rewrite this run's session contract as one same-directory file replacement and advance its
    /// revision. False leaves an absent or unreadable session untouched.</summary>
    public static bool RewriteSession(string glbPath,
        Func<BlenderSessionDocument, BlenderSessionDocument> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (ReadSessionDocument(glbPath) is not { } current) return false;
        long currentRevision = current.Revision;
        var updated = update(current)
            ?? throw new InvalidOperationException("A session rewrite returned no session document.");
        updated = updated with
        {
            Parts = updated.Parts ?? new List<SessionPart>(),
            Revision = checked(currentRevision + 1),
        };
        WriteSessionDocument(SessionPath(glbPath), updated);
        return true;
    }

    /// <summary>Acknowledge one accepted send as the next live state of this Blender run. The raw return is
    /// first copied to a revisioned immutable geometry baseline. Changed per-part workspaces are promoted
    /// beside it, then the target document is atomically replaced with current exact-slot rows. The session
    /// revision is written LAST: the Blender side treats that advance as the acknowledgement that all prior
    /// artifacts and routing metadata are ready.</summary>
    public static bool AcknowledgeReturn(string glbPath, string returnGlb,
        Func<BlenderSessionDocument, BlenderSessionDocument> updateSession,
        IReadOnlyList<BlenderTargetAcknowledgement>? targetUpdates = null)
    {
        ArgumentNullException.ThrowIfNull(updateSession);
        if (ReadSessionDocument(glbPath) is not { } currentSession
            || ReadTargetDoc(returnGlb) is not { } currentTarget)
            return false;

        long nextRevision = checked(currentSession.Revision + 1);
        var updatedSession = updateSession(currentSession)
            ?? throw new InvalidOperationException("A session acknowledgement returned no session document.");
        updatedSession = updatedSession with
        {
            Parts = updatedSession.Parts ?? new List<SessionPart>(),
            Revision = nextRevision,
        };

        var updates = new Dictionary<string, BlenderTargetAcknowledgement>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in targetUpdates ?? Array.Empty<BlenderTargetAcknowledgement>())
            if (!updates.TryAdd(update.Target.Part, update))
                throw new InvalidDataException("A return acknowledgement names one part more than once.");
        foreach (string part in updates.Keys)
            if (!currentTarget.Targets.Any(target =>
                    string.Equals(target.Part, part, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The return target document has no part '{part}'.");

        string baselineRoot = Path.Combine(TargetDocDirectory(returnGlb), ".gf2baselines",
            $"revision-{nextRevision:D8}-{Guid.NewGuid():N}");
        bool targetWritten = false;
        try
        {
            Directory.CreateDirectory(baselineRoot);
            string comparison = Path.Combine(baselineRoot, "comparison.glb");
            File.Copy(returnGlb, comparison, overwrite: false);

            int index = 0;
            var promoted = new Dictionary<string, BlenderSessionTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var (part, update) in updates)
            {
                string sourceWorkspace = Path.GetFullPath(update.PreparedWorkspace);
                if (!File.Exists(sourceWorkspace))
                    throw new FileNotFoundException("The prepared Blender comparison workspace is missing.",
                        sourceWorkspace);
                string destinationDirectory = Path.Combine(baselineRoot, $"part-{index++:D4}");
                CopyDirectory(Path.GetDirectoryName(sourceWorkspace)!, destinationDirectory);
                string destinationWorkspace = Path.Combine(destinationDirectory,
                    Path.GetFileName(sourceWorkspace));
                promoted.Add(part, update.Target with
                {
                    Workspace = Path.GetFullPath(destinationWorkspace),
                    IngressReturn = update.Target.IngressReturn is null ? null
                        : Path.GetFullPath(update.Target.IngressReturn),
                });
            }

            var updatedTarget = new TargetDoc
            {
                SessionId = currentTarget.SessionId,
                OpenedGlb = Path.GetRelativePath(TargetDocDirectory(returnGlb), comparison),
                SessionGlb = currentTarget.SessionGlb ?? currentTarget.OpenedGlb,
                Targets = currentTarget.Targets.Select(target =>
                    promoted.TryGetValue(target.Part, out var replacement) ? replacement : target).ToList(),
            };
            WriteTargetDocument(TargetPath(returnGlb), updatedTarget);
            targetWritten = true;
            WriteSessionDocument(SessionPath(glbPath), updatedSession);
            return true;
        }
        finally
        {
            if (!targetWritten)
                try { if (Directory.Exists(baselineRoot)) Directory.Delete(baselineRoot, recursive: true); }
                catch (IOException) { /* never mask the acknowledgement failure over orphan cleanup */ }
                catch (UnauthorizedAccessException) { /* same */ }
        }
    }

    private static void WriteSessionDocument(string path, BlenderSessionDocument doc)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(doc, SessionJson));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* never mask the session write result over temp cleanup */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
    }

    private static void WriteTargetDocument(string path, TargetDoc doc)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(doc, SessionJson));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* never mask the target write result over temp cleanup */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
    }

    /// <summary>The identity-addressed destinations of an app-created return. Empty keeps compatibility with
    /// hand-authored and older sessions, whose destination is handled by the legacy fallback.</summary>
    public static IReadOnlyList<BlenderSessionTarget> ReadReturnTargets(string returnGlb)
    {
        if (ReadTargetDoc(returnGlb) is not { } doc) return Array.Empty<BlenderSessionTarget>();
        return doc.Targets.Where(t => !string.IsNullOrWhiteSpace(t.Part)
            && ((t.IsExactSlot && Path.IsPathFullyQualified(t.Workspace))
                || t.IsPartRoute
                || (!string.IsNullOrWhiteSpace(t.ProjectAssetId)
                    && Path.IsPathFullyQualified(t.Workspace)))).ToList();
    }

    /// <summary>Whether a present return-target document parsed as an app session. This distinguishes a
    /// missing legacy address from a corrupt address that must never fall through to legacy routing.</summary>
    public static bool ReturnTargetMetadataReadable(string returnGlb) => ReadTargetDoc(returnGlb) is not null;

    /// <summary>Whether a session file exists beside the opened glb, independently of whether it can be read.</summary>
    public static bool SessionMetadataExists(string glbPath) => File.Exists(SessionPath(glbPath));

    /// <summary>The immutable glb every part of the next return is compared against to tell a real edit from
    /// a part that merely came back along with the rest (<see cref="Mesh.SendBackGeometry.Unchanged"/>).
    /// Initially it is the launch composition; after each accepted intake it is a revisioned copy of that
    /// raw return. A combined session's parts each have a workspace glb of their own, and none is this file.
    ///
    /// <para>Resolved against the target document's OWN folder, because that is what survives the mod
    /// folder being renamed while Blender is open — an absolute path recorded at launch names a folder that
    /// no longer exists, and a baseline that cannot be found reads as "cannot tell" and takes every part.
    /// A document that recorded an absolute path is read as given.</para>
    ///
    /// <para>Null where the return names none: a hand-written session, or one an older build wrote before
    /// the field existed. The caller must then take every part rather than drop an edit it could not
    /// read.</para></summary>
    public static string? ReadReturnBaseline(string returnGlb)
    {
        return ResolveTargetPath(returnGlb, ReadTargetDoc(returnGlb)?.OpenedGlb);
    }

    /// <summary>The glb whose adjacent live session document owns this return. Unlike
    /// <see cref="ReadReturnBaseline"/>, this stays fixed when acknowledgements advance the comparison
    /// artifact. Older target documents use their original opened-glb field for both answers.</summary>
    public static string? ReadReturnSessionGlb(string returnGlb)
    {
        var doc = ReadTargetDoc(returnGlb);
        return ResolveTargetPath(returnGlb, doc?.SessionGlb ?? doc?.OpenedGlb);
    }

    private static string? ResolveTargetPath(string returnGlb, string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return null;
        try
        {
            return Path.IsPathFullyQualified(recorded)
                ? recorded : Path.GetFullPath(recorded, TargetDocDirectory(returnGlb));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException
                                  or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The folder the target document beside <paramref name="returnGlb"/> lives in — what its
    /// recorded paths are written relative to and read back against.</summary>
    private static string TargetDocDirectory(string returnGlb) =>
        Path.GetDirectoryName(Path.GetFullPath(TargetPath(returnGlb))) ?? "";

    /// <summary>The return's session document, or null where there isn't one this build can read.</summary>
    private static TargetDoc? ReadTargetDoc(string returnGlb)
    {
        try
        {
            var path = TargetPath(returnGlb);
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<TargetDoc>(File.ReadAllText(path), SessionJson);
            return doc is null || string.IsNullOrWhiteSpace(doc.SessionId) ? null
                : new TargetDoc
                {
                    SessionId = doc.SessionId,
                    OpenedGlb = doc.OpenedGlb,
                    SessionGlb = doc.SessionGlb,
                    Targets = doc.Targets ?? new List<BlenderSessionTarget>(),
                };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                  or ArgumentException or NotSupportedException)
        {
            return null;
        }
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

    /// <summary>The edit destination each returned part selected, per the send sidecar. Existing string ids
    /// and new-edit objects share the established <c>editIds</c> key. Empty when the sidecar carries none or
    /// cannot be read, preserving the pre-selection intake behavior. Part names are case-insensitive; a
    /// sidecar that spells the same part twice with different casing is invalid rather than ambiguous.</summary>
    public static IReadOnlyDictionary<string, BlenderPartTarget> ReadEditIds(string sidecarPath)
    {
        Dictionary<string, BlenderPartTarget>? ids;
        try
        {
            var doc = JsonSerializer.Deserialize<SendDoc>(File.ReadAllText(sidecarPath), SessionJson);
            ids = doc?.EditIds;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                  or ArgumentException)
        {
            return EmptyEditIds;
        }
        if (ids is not { Count: > 0 }) return EmptyEditIds;
        var result = new Dictionary<string, BlenderPartTarget>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (part, target) in ids)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (!seen.Add(part))
                throw new AuthoredRefusalException(
                    $"The send sidecar names part '{part}' more than once with different capitalization.");
            if (target is not null && (target.IsExisting || target.IsNew)) result.Add(part, target);
        }
        return result;
    }

    private static readonly IReadOnlyDictionary<string, BlenderPartTarget> EmptyEditIds =
        new Dictionary<string, BlenderPartTarget>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Write a send sidecar beside <paramref name="glbPath"/>, carrying what
    /// <see cref="ReadHiddenParts"/> and <see cref="ReadEditIds"/> read back out of it.
    ///
    /// <para>The bridge script writes this file; the app writes one only to PUT A SEND BACK — a return it
    /// took and then could not apply, whose sidecar the watcher's ingest had already consumed. Restoring
    /// the marker is what lets the next open of that mod rediscover the send through
    /// <see cref="BlenderSendWatcher.ScanExisting"/>; the glb it names was never touched.</para></summary>
    public static void WriteSendSidecar(string glbPath, IReadOnlyList<string>? hiddenParts,
        IReadOnlyDictionary<string, BlenderPartTarget>? editIds)
    {
        var doc = new SendDoc
        {
            HiddenParts = hiddenParts is null ? new List<string>() : new List<string>(hiddenParts),
            EditIds = editIds is null || editIds.Count == 0 ? null
                : new Dictionary<string, BlenderPartTarget>(editIds, StringComparer.Ordinal),
        };
        File.WriteAllText(SidecarPath(glbPath), JsonSerializer.Serialize(doc, SessionJson));
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
    /// <para>The read is LENIENT, as every other read of a Blender-written glb is: this is an external
    /// ingress artifact, and a schema complaint about accessors this app never reads must not prevent the
    /// normalizer from recovering the modder's edit.</para></summary>
    public static IncomingEdit ReadSend(string glbPath, string? meshName = null)
    {
        var sidecar = SidecarPath(glbPath);
        if (meshName is null && MeshGltf.MeshNames(glbPath).Count == 0)
            return new IncomingEdit(null, glbPath, false, ReadHiddenParts(sidecar), ReadEditIds(sidecar));
        var mesh = MeshGltf.ImportGlb(glbPath, meshName, lenient: true);
        bool nodeMoved = false;
        try { nodeMoved = MeshGltf.HasNonIdentityNodeTransform(glbPath, meshName); }
        catch { /* detection is advisory — never fail a real edit over it */ }
        return new IncomingEdit(mesh, glbPath, nodeMoved, ReadHiddenParts(sidecar), ReadEditIds(sidecar));
    }
}

/// <summary>Watches a mod's send folder and raises <see cref="EditReceived"/> with the imported mesh.
/// Fires on the sidecar (written last) so the glb is complete; debounces the duplicate Created/Changed
/// events Windows emits per write.
///
/// <para>The sidecar is also the UNHANDLED-send marker: it is deleted once the send has been read back, so
/// <see cref="ScanExisting"/> can pick up a send that landed with no watcher listening and never pick the
/// same one up twice.</para>
///
/// <para>What it watches is ONE mod's folder, however deep the send sits in it. A send under a folder that
/// holds a project of its own belongs to that mod and is left alone — see
/// <see cref="BelongsToANestedProject"/>.</para></summary>
public sealed class BlenderSendWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly string _sendDir;
    private readonly SearchOption _scanDepth;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public event Action<IncomingEdit>? EditReceived;
    /// <summary>A Send landed but could not be read back: (glb path, the failure). The exception travels
    /// whole so the surface can route by type — an <see cref="AuthoredRefusalException"/> was written for
    /// the modder and is shown; anything else is a diagnosis of the read. App-created sessions write
    /// distinct return artifacts, so a failed read leaves the canonical workspace file untouched. Older
    /// overwrite-in-place session files are still reported by their workspace path.</summary>
    public event Action<string, Exception>? Error;

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
    /// so every one of those sends is HANDED to the subscriber before the caller's next line — what the
    /// subscriber then does with it is its own business — which is also why it takes the one-attempt route:
    /// a scan's sends were written before this scan existed, so a wait for a writer that isn't there would
    /// be the calling thread standing still, once per bad send.</summary>
    public void ScanExisting()
    {
        string[] pending;
        try { pending = Directory.GetFiles(_sendDir, "*" + BlenderBridge.SidecarSuffix, _scanDepth); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Array.Sort(pending, (a, b) => LastWrite(a).CompareTo(LastWrite(b)));
        foreach (var sidecar in pending)
        {
            // Handing a send to the subscriber is handing it the watcher too: disposing from inside the
            // ingest is something a caller may do, and what it says is that this folder is no longer the
            // one being watched. The rest of THIS list is then somebody else's to take.
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
        if (BelongsToANestedProject(sidecarPath)) return;
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
        catch (GlbHeldOpenException ex) when (keepOnLockedGlb) { Error?.Invoke(glb, ex); }
        catch (Exception ex) { ConsumeSidecar(sidecarPath); Error?.Invoke(glb, ex); }
    }

    /// <summary>Whether this send belongs to a DIFFERENT mod that happens to live inside the watched one.
    /// Mods are ordinary folders and nothing stops the modder from keeping one inside another; this watcher
    /// walks subdirectories, so without this it would take the inner mod's sends and hand them to the outer
/// mod's document — where a part route's subject and outfit resolve just as well, and land the modder's
    /// work in the wrong mod.
    ///
    /// <para>A send is the inner mod's when any folder between this watcher's root and the sidecar holds a
    /// project of its own. It is left exactly where it is, sidecar and all, so opening the mod that owns it
    /// takes it through the ordinary scan.</para>
    ///
    /// <para>A folder that cannot be walked answers false: this decides whether to SKIP a send, and a send
    /// dropped over an unreadable path is a send lost.</para></summary>
    private bool BelongsToANestedProject(string sidecarPath)
    {
        try
        {
            string root = Path.GetFullPath(_sendDir);
            for (var folder = Directory.GetParent(Path.GetFullPath(sidecarPath));
                 folder is not null && !PathsEqual(folder.FullName, root);
                 folder = folder.Parent)
                if (File.Exists(ModProject.ManifestPathFor(folder.FullName))) return true;
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                  or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }

        static bool PathsEqual(string a, string b) => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drop the handled send's sidecar. Consumed on a reported failure too, except where
    /// <see cref="Ingest"/> keeps it: the failure has been reported and a re-read at the next open would
    /// fail identically. The raw return glb remains available for inspection/recovery. Best-effort — a
    /// locked sidecar costs a duplicate ingest, never the edit.</summary>
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
        throw new IOException(
            $"the file Blender sent back never finished writing: {Path.GetFileName(path)}", last);
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
        string why = $"the file Blender sent back couldn't be opened: {Path.GetFileName(path)}";
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
