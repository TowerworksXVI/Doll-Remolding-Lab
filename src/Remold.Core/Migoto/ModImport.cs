using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Remold.Core.Mesh;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>Why a built mod folder cannot be read back into a project. Each cause is a distinct thing the
/// folder says about itself, and the three "no record" causes are deliberately separate: they carry
/// different answers for the person holding the mod.</summary>
public enum ImportRefusalCause
{
    /// <summary>Nothing in the picked folder identifies it as a mod this app built.</summary>
    NotAModFolder,
    /// <summary>A zip that opened and holds no mod anywhere this route looks.</summary>
    NoModInZip,
    /// <summary>The mod predates repair data. Nothing was withheld; only its author can rebuild it.</summary>
    BuiltBeforeRepairData,
    /// <summary>The mod is new enough to carry the record and does not. A statement about the folder, never
    /// about the author: whether the option was cleared deliberately cannot be told from here.</summary>
    BuiltWithoutRepairData,
    /// <summary>The folder carries no record and no version to explain the absence.</summary>
    NoRepairData,
    /// <summary>A schema-1 record, which only a pre-release build wrote.</summary>
    PreReleaseBuild,
    /// <summary>A record written by a schema this app does not read.</summary>
    NewerApp,
    /// <summary>A record this app reads but cannot make sense of, or a file beside it the app could not
    /// read at all.</summary>
    UnreadableRepairData,
    /// <summary>A file the record names is not in the folder.</summary>
    MissingFile,
    /// <summary>The zip could not be opened.</summary>
    ZipUnreadable,
    /// <summary>The zip or folder holds more than one mod.</summary>
    MoreThanOneMod,
    /// <summary>The game files have not been read yet, so nothing can be imported.</summary>
    ScanRunning,
    /// <summary>A replacement compiled in scene-rest space whose rest pose the record does not carry.</summary>
    UnrecordedRest,
    /// <summary>A recorded rest pose the app cannot put back.</summary>
    UnreadableRest,
}

/// <summary>One refusal, with the sentence shown for it.</summary>
public sealed record ImportRefusal(ImportRefusalCause Cause, string Message);

/// <summary>A built mod folder read far enough to say what it is, without anything written yet. Disposing
/// it removes the extraction a zip import made; a folder import owns nothing and disposes to nothing.</summary>
public sealed class ImportableMod : IDisposable
{
    /// <summary>The folder holding <c>gf2mod.json</c>: the picked folder, or the one mod folder inside the
    /// extraction of the picked zip.</summary>
    public required string Folder { get; init; }

    /// <summary>The name the picked thing is reported under: the folder's name, or the zip file's.</summary>
    public required string PickedName { get; init; }

    public required string ModName { get; init; }
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? Character { get; init; }
    public string? Outfit { get; init; }
    /// <summary>The preview image's file name inside the mod folder, when the mod ships one.</summary>
    public string? PreviewFile { get; init; }

    /// <summary>The preview image the mod NAMES and does not hold, when that is what happened. The import
    /// goes ahead without a preview — the picture is how a mod manager lists the mod and no part of what it
    /// changes — and this is what a surface writes to the log about it.</summary>
    public string? MissingPreviewFile { get; init; }

    public required RepairData.Payload Payload { get; init; }

    internal string? ExtractedTo { get; init; }

    public void Dispose()
    {
        if (ExtractedTo is null) return;
        try { Directory.Delete(ExtractedTo, recursive: true); }
        catch { /* the extraction is regenerable; a leftover costs only disk */ }
    }
}

/// <summary>Either a refusal or the mod, never both.</summary>
public sealed record ImportInspection(ImportRefusal? Refusal, ImportableMod? Mod)
{
    public static ImportInspection Refused(ImportRefusalCause cause, string message) =>
        new(new ImportRefusal(cause, message), null);
    public static ImportInspection Accepted(ImportableMod mod) => new(null, mod);
}

/// <summary>
/// Reading a BUILT mod folder back into a schema-2 project.
///
/// <para>Two steps, deliberately apart: <see cref="Inspect"/> answers what the folder is and writes
/// nothing, so a surface can ask the person about the mod's author before anything lands on disk;
/// <see cref="Materialize"/> then writes the project into a folder the caller has already minted.</para>
///
/// <para>What is reconstructed is the modder's intent and the identity it was pointed at. Everything the
/// build reads from the game is re-read at the next build, so none of it is invented here.</para>
/// </summary>
public static partial class ModImport
{
    /// <summary>The sidecar every build writes, whatever else it carries.</summary>
    public const string SidecarName = ModInstall.SidecarName;

    /// <summary>The first release that shipped repair data. A mod stamped older than this predates the
    /// record; one stamped this or newer was built without it.</summary>
    private static readonly Version RepairDataFrom = new(0, 4, 0);

    /// <summary>How deep inside a picked folder or zip a mod folder may sit. An archive made from the mod
    /// folder nests it once; one made from a folder holding it nests it twice.</summary>
    private const int MaxDepth = 2;

    /// <summary>Where the import cache folder sits under the app's cache root.</summary>
    private static string DefaultTempRoot => LabPaths.ImportCacheRoot;

    // ---- the sentences ------------------------------------------------------------------------------

    public static string NotAModFolderMessage(string name) => $"'{name}' is not a mod folder.";

    public static string NoModInZipMessage(string name) => $"'{name}' does not contain a mod.";

    public static string BuiltBeforeRepairDataMessage(string name, string version) =>
        $"'{name}' is not editable. It was built by version {version}, and only its author can rebuild it.";

    public static string BuiltWithoutRepairDataMessage(string name) => $"'{name}' is not editable.";

    public static string NoRepairDataMessage(string name) =>
        $"'{name}' is not editable, and it does not say which version built it.";

    public static string PreReleaseBuildMessage(string name) =>
        $"'{name}' is not editable. It was built by a pre-release version, and only its author can "
        + "rebuild it.";

    public static string NewerAppMessage(string name) =>
        $"'{name}' was built by a newer version of Doll Remolding Lab. Update the app to import it.";

    /// <summary>What a record, or a file beside it, that would not read says. The reason is the reader's
    /// own, with the machine's paths taken out of it.</summary>
    public static string UnreadableMessage(string name, string reason) =>
        $"Couldn't read '{name}': {reason}.";

    public static string MissingFileMessage(string name, string file) =>
        $"'{name}' is missing the file '{file}'.";

    public static string ZipUnreadableMessage(string name, string reason) =>
        $"Couldn't open '{name}': {reason}.";

    public static string MoreThanOneModMessage(string name) =>
        $"'{name}' contains more than one mod. Import one at a time.";

    /// <summary>What a mod cannot be imported for once the reading has started. The reason names what the
    /// record could not supply, so the two import-time refusals below read as one shape.</summary>
    public static string ImportFailedMessage(string name, string reason) =>
        $"Couldn't import '{name}': {reason}.";

    public static string UnrecordedRestMessage(string name, string part) => ImportFailedMessage(name,
        $"the new mesh for '{part}' was saved without its rest pose");

    public static string UnreadableRestMessage(string name, string part) => ImportFailedMessage(name,
        $"the rest pose recorded for '{part}' can't be read");

    /// <summary>What a mod holding a hide says when the install no longer names the part. The reason side
    /// of <see cref="ImportFailedMessage"/>, because it is thrown out of the reconstruction.</summary>
    public static string PartGoneReason(string part) =>
        $"the game files no longer contain '{part}', which this mod hides";

    /// <summary>What a part reading a shading value off another part says when neither the record nor the
    /// install says where the material the value came from sits. The reason side of
    /// <see cref="ImportFailedMessage"/>, because it is thrown out of the reconstruction.</summary>
    public static string UndescribedSourcePartReason(string part) =>
        $"'{part}' takes a value from a part its repair data does not describe";

    /// <summary>A file name in the record that does not land inside the mod folder.</summary>
    public static string EscapingFileReason(string file) =>
        $"the file name '{file}' points outside the mod";

    /// <summary>What every import says while the install is still being read. An import needs the game
    /// files: a hide states only that a part does not draw, and which objects that is comes from the
    /// install alone.</summary>
    public const string ScanRunningMessage =
        "Cannot import a mod while the game files are being read. Try again when the scan finishes.";

    // ---- the author check ---------------------------------------------------------------------------

    /// <summary>Whether the person importing has to be told whose work this is: the mod lists an author
    /// who is not them, or this app has no author set at all — a blank setting names nobody, so it can
    /// never stand in for a match.</summary>
    public static bool AuthorDiffers(string? listed, string? settings)
    {
        string mine = (settings ?? "").Trim();
        if (mine.Length == 0) return true;
        return !string.Equals((listed ?? "").Trim(), mine, StringComparison.OrdinalIgnoreCase);
    }

    // ---- inspection ---------------------------------------------------------------------------------

    /// <summary>Read <paramref name="folderOrZip"/> far enough to say whether it can be imported, writing
    /// nothing outside <paramref name="tempRoot"/>. The path may be a mod folder, a folder holding one, the
    /// <c>gf2mod.json</c> or <c>repair.json</c> inside one, or a distribution zip.</summary>
    /// <param name="gameFilesRead">Whether the install has been read. False refuses before anything else is
    /// looked at: every import needs the game files, so asking about the mod first would put a question on
    /// screen for an answer that cannot be acted on.</param>
    /// <param name="tempRoot">Where a zip is extracted. Null uses the app's own cache root; tests pass
    /// their own. The extraction belongs to the returned <see cref="ImportableMod"/> and is removed when it
    /// is disposed.</param>
    public static ImportInspection Inspect(string folderOrZip, bool gameFilesRead, string? tempRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderOrZip);
        if (!gameFilesRead)
            return ImportInspection.Refused(ImportRefusalCause.ScanRunning, ScanRunningMessage);

        string full = Path.GetFullPath(folderOrZip);

        if (Directory.Exists(full)) return InspectTree(full, Path.GetFileName(TrimSeparator(full)), null);

        string leaf = Path.GetFileName(full);
        if (string.Equals(leaf, SidecarName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, RepairData.FileName, StringComparison.OrdinalIgnoreCase))
        {
            string folder = Path.GetDirectoryName(full)
                ?? throw new InvalidDataException($"'{leaf}' has no containing folder");
            return InspectFolder(folder, Path.GetFileName(TrimSeparator(folder)), null);
        }

        return InspectZip(full, leaf, tempRoot);
    }

    private static string TrimSeparator(string path) => Path.TrimEndingDirectorySeparator(path);

    private static ImportInspection InspectZip(string zip, string name, string? tempRoot)
    {
        if (!File.Exists(zip))
            return ImportInspection.Refused(ImportRefusalCause.NotAModFolder, NotAModFolderMessage(name));

        string root = tempRoot ?? DefaultTempRoot;
        string dest = Path.Combine(root, "zip-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dest);
            ZipFile.ExtractToDirectory(zip, dest);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            TryDelete(dest);
            return ImportInspection.Refused(ImportRefusalCause.ZipUnreadable,
                ZipUnreadableMessage(name, Strip(ex.Message, zip, dest)));
        }

        // Everything after the extraction sweeps it unless a mod comes back holding it: a throw out of the
        // reads below would otherwise leave the whole archive behind in the cache.
        ImportInspection inspection;
        try { inspection = InspectTree(dest, name, dest, zipped: true); }
        catch { TryDelete(dest); throw; }
        if (inspection.Mod is null) TryDelete(dest);
        return inspection;
    }

    /// <summary>The one mod folder at depth 0..<see cref="MaxDepth"/> under <paramref name="root"/>,
    /// inspected. A picked folder and an extracted zip take the same walk: a modder who picks the folder
    /// they downloaded into, rather than the mod folder inside it, is naming the same one mod.</summary>
    private static ImportInspection InspectTree(string root, string name, string? extractedTo,
        bool zipped = false)
    {
        var folders = ModFoldersIn(root);
        if (folders.Count == 0)
            return zipped
                ? ImportInspection.Refused(ImportRefusalCause.NoModInZip, NoModInZipMessage(name))
                : ImportInspection.Refused(ImportRefusalCause.NotAModFolder, NotAModFolderMessage(name));
        if (folders.Count > 1)
            return ImportInspection.Refused(ImportRefusalCause.MoreThanOneMod, MoreThanOneModMessage(name));
        return InspectFolder(folders[0], name, extractedTo);
    }

    /// <summary>Every directory at depth 0..<see cref="MaxDepth"/> under <paramref name="root"/> holding a
    /// sidecar, in a settled order so two reads of one tree answer the same folder.</summary>
    private static List<string> ModFoldersIn(string root)
    {
        var found = new List<string>();
        Walk(root, 0);
        return found;

        void Walk(string dir, int depth)
        {
            if (File.Exists(Path.Combine(dir, SidecarName))) { found.Add(dir); return; }
            if (depth >= MaxDepth) return;
            foreach (var child in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                Walk(child, depth + 1);
        }
    }

    private static ImportInspection InspectFolder(string folder, string name, string? extractedTo)
    {
        string sidecarPath = Path.Combine(folder, SidecarName);
        if (!File.Exists(sidecarPath))
            return ImportInspection.Refused(ImportRefusalCause.NotAModFolder, NotAModFolderMessage(name));

        Sidecar sidecar;
        RepairData.Payload payload;
        int? schema;
        try
        {
            sidecar = Sidecar.Read(sidecarPath);
            string repairPath = Path.Combine(folder, RepairData.FileName);
            if (!File.Exists(repairPath)) return NoRecord(name, sidecar.AppVersion);

            // The schema is read before the record is: a number this app does not carry has its own answer,
            // and reading it off the reader's own refusal message would guess at what that message says.
            schema = PeekSchema(repairPath);
            if (schema == RepairData.LegacySchema)
                return ImportInspection.Refused(ImportRefusalCause.PreReleaseBuild,
                    PreReleaseBuildMessage(name));
            if (schema is { } value && value != RepairData.Schema)
                return ImportInspection.Refused(value > RepairData.Schema
                    ? ImportRefusalCause.NewerApp : ImportRefusalCause.UnreadableRepairData,
                    value > RepairData.Schema ? NewerAppMessage(name)
                        : UnreadableMessage(name, $"it states schema {value}"));

            // A record carrying a field this app's writer never writes was written by another build under
            // the same schema number. The reader would ignore that field and hand back a record that looks
            // smaller than it is — a pre-release build's placements come back collapsed into Always — so
            // the whole record is refused instead, by the one thing that is true of it.
            if (RepairData.FirstUnknownProperty(repairPath) is not null)
                return ImportInspection.Refused(ImportRefusalCause.PreReleaseBuild,
                    PreReleaseBuildMessage(name));

            try { payload = RepairData.Read(repairPath); }
            catch (InvalidDataException ex)
            {
                return ImportInspection.Refused(ImportRefusalCause.UnreadableRepairData,
                    UnreadableMessage(name, Strip(ex.Message, repairPath, folder)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file held open, a folder that will not be entered, a drive that went away. The person
            // picked this folder, so the read's own diagnosis is what they can act on — not "not a mod
            // folder", which says the opposite of what happened.
            return ImportInspection.Refused(ImportRefusalCause.UnreadableRepairData,
                UnreadableMessage(name, Strip(ex.Message, folder)));
        }

        if (FirstUnreadableFile(folder, payload) is { } unreadable)
            return unreadable.Escapes
                ? ImportInspection.Refused(ImportRefusalCause.UnreadableRepairData,
                    UnreadableMessage(name, EscapingFileReason(unreadable.File)))
                : ImportInspection.Refused(ImportRefusalCause.MissingFile,
                    MissingFileMessage(name, unreadable.File));

        // A replacement compiled into scene-rest space with no rest pose of its own was stood up by the
        // INSTALL the mod was built on, and that stand is nowhere in the folder. Reading it back would put
        // geometry into the project that a rebuild stands up a second time, which is a visible deformation
        // and nothing the modder could see coming, so it is refused here instead.
        if (FirstUnstateableRest(payload) is { } unstateable)
            return ImportInspection.Refused(ImportRefusalCause.UnrecordedRest,
                UnrecordedRestMessage(name, unstateable));

        // A rest the reconstruction cannot put back would ship geometry in the wrong space, for the same
        // reason. The read happens here so the mod is refused before the library is touched.
        if (FirstUnreadableRest(payload) is { } unreadableRest)
            return ImportInspection.Refused(ImportRefusalCause.UnreadableRest,
                UnreadableRestMessage(name, unreadableRest));

        return ImportInspection.Accepted(new ImportableMod
        {
            Folder = folder,
            PickedName = name,
            ModName = string.IsNullOrWhiteSpace(sidecar.Name) ? name : sidecar.Name!,
            Author = sidecar.Author,
            Version = sidecar.Version,
            Description = sidecar.Description,
            Character = sidecar.Character,
            Outfit = sidecar.Outfit,
            PreviewFile = HeldPreview(folder, sidecar.Preview),
            MissingPreviewFile = sidecar.Preview is { Length: > 0 } named
                && HeldPreview(folder, named) is null ? named : null,
            Payload = payload,
            ExtractedTo = extractedTo,
        });
    }

    /// <summary>The preview the sidecar names, as a name inside the mod folder that the folder actually
    /// holds. Null where it names none, where the name leaves the folder, or where the file is gone.</summary>
    private static string? HeldPreview(string folder, string? preview) =>
        preview is { Length: > 0 } named && ShippedPathOrNull(folder, named) is { } full
        && File.Exists(full) ? named : null;

    /// <summary>The three answers a missing record has. They are not one answer: a mod that predates the
    /// record, one built without it, and one that says nothing about when it was built carry different
    /// things for the person holding the folder.</summary>
    private static ImportInspection NoRecord(string name, string? appVersion)
    {
        if (ParseAppVersion(appVersion) is not { } version)
            return ImportInspection.Refused(ImportRefusalCause.NoRepairData, NoRepairDataMessage(name));
        return version < RepairDataFrom
            ? ImportInspection.Refused(ImportRefusalCause.BuiltBeforeRepairData,
                BuiltBeforeRepairDataMessage(name, appVersion!.Trim()))
            : ImportInspection.Refused(ImportRefusalCause.BuiltWithoutRepairData,
                BuiltWithoutRepairDataMessage(name));
    }

    /// <summary>The sidecar's <c>app_version</c> as a comparable version: a leading <c>v</c> and any
    /// pre-release or build suffix come off first. Null where the stamp is absent or is not a version,
    /// which is a different answer from an old one.</summary>
    public static Version? ParseAppVersion(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return null;
        string text = stamp.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1])) text = text[1..];
        int cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? version : null;
    }

    private static int? PeekSchema(string repairPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(repairPath));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("schema", out var schema)
                && schema.TryGetInt32(out int value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A file the record names that this app will not read: one whose name leaves the mod folder,
    /// or one the folder does not hold. Named rather than counted, so the person can see which file it
    /// is.</summary>
    private sealed record UnreadableFile(string File, bool Escapes);

    /// <inheritdoc cref="UnreadableFile"/>
    private static UnreadableFile? FirstUnreadableFile(string folder, RepairData.Payload payload)
    {
        foreach (string file in RecordedFiles(payload))
        {
            if (ShippedPathOrNull(folder, file) is not { } full) return new UnreadableFile(file, true);
            if (!File.Exists(full)) return new UnreadableFile(file, false);
        }
        return null;
    }

    /// <summary>Every file the record names, in the record's own order. THE list both the inspection and
    /// the reconstruction read a file name off. The sidecar's preview is deliberately NOT one of them: it
    /// is how a mod manager lists the mod, not part of what the mod changes, so a mod that lost it still
    /// imports.</summary>
    private static IEnumerable<string> RecordedFiles(RepairData.Payload payload)
    {
        foreach (var change in payload.Changes)
        {
            if (change.Geometry is { } geometry)
            {
                foreach (var stream in geometry.Streams) yield return stream.File;
                yield return geometry.IndexFile;
            }
            foreach (var row in change.Textures ?? Array.Empty<RepairData.SubmeshRecord>())
                foreach (var slot in Slots(row))
                    if (slot.File is { } file) yield return file;
        }
        foreach (var pick in payload.StockRamps ?? Array.Empty<RepairData.StockRampRecord>())
            yield return pick.Ramp;
    }

    /// <summary>One file the record names, as a path inside the mod folder, or null when the name does not
    /// land there. The record travelled with the mod and is as untrusted as the mod is: a name holding
    /// <c>..</c>, a rooted path or a drive letter would otherwise let an import read whatever it pointed
    /// at. THE gate, so the inspection and the reconstruction refuse the same names.</summary>
    internal static string? ShippedPathOrNull(string folder, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
            + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(folder, file)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <inheritdoc cref="ShippedPathOrNull"/>
    internal static string ShippedPath(string folder, string file) =>
        ShippedPathOrNull(folder, file) ?? throw new InvalidDataException(EscapingFileReason(file));

    /// <inheritdoc cref="InspectFolder"/>
    private static string? FirstUnstateableRest(RepairData.Payload payload) => payload.Changes
        .FirstOrDefault(change =>
            string.Equals(change.Geometry?.Union?.Space, "scene_rest", StringComparison.Ordinal)
            && change.BakedRest is null)?.Mesh;

    /// <summary>Whether two references name one object of the install: which bundle holds it and which
    /// object of that bundle it is. A name is the object's label, never its identity.</summary>
    private static bool SameGameAsset(GameAssetRef? left, GameAssetRef? right) =>
        left is not null && right is not null && left.PathId == right.PathId
        && string.Equals(left.LogicalBundle, right.LogicalBundle, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="InspectFolder"/>
    private static string? FirstUnreadableRest(RepairData.Payload payload) => payload.Changes
        .FirstOrDefault(change =>
        {
            RestBake.FromList(change.BakedRest, out bool refused);
            return refused;
        })?.Mesh;

    internal static IEnumerable<RepairData.SlotRecord> Slots(RepairData.SubmeshRecord row)
    {
        if (row.Albedo is { } albedo) yield return albedo;
        if (row.Normal is { } normal) yield return normal;
        if (row.Rmo is { } rmo) yield return rmo;
        if (row.Ramp is { } ramp) yield return ramp;
        if (row.Blend is { } blend) yield return blend;
        foreach (var property in row.Textures ?? Array.Empty<RepairData.PropertySlotRecord>())
            yield return property.Slot;
    }

    /// <summary>The reason side of a refusal, for an exception that came out of a read or a reconstruction
    /// rather than out of a refusal of its own. What the record could not supply, or what the machine hit,
    /// each with the paths taken out; anything else names nothing the modder can act on.</summary>
    public static string FailureReason(Exception exception, string source) => exception switch
    {
        InvalidDataException or IOException or UnauthorizedAccessException =>
            Strip(exception.Message, source),
        _ => "it describes something this app can't rebuild",
    };

    /// <summary>A reader's own message with the machine's paths taken out of it: the person picked a folder,
    /// and a temp path from this run names nothing they can act on.</summary>
    private static string Strip(string message, params string?[] paths)
    {
        string text = message;
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            text = text.Replace(path, "", StringComparison.OrdinalIgnoreCase);
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                text = text.Replace(dir, "", StringComparison.OrdinalIgnoreCase);
        }
        return text.Trim().TrimEnd('.').TrimEnd(':', ' ').Trim();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort: the extraction is regenerable */ }
    }

    /// <summary>The frozen sidecar's fields this route reads. Everything else in it is a mod manager's
    /// business.</summary>
    internal sealed record Sidecar(string? Name, string? Version, string? Author, string? Description,
        string? Preview, string? Character, string? Outfit, string? AppVersion)
    {
        internal static Sidecar Read(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return new Sidecar(Text(root, "name"), Text(root, "version"), Text(root, "author"),
                    Text(root, "description"), Text(root, "preview"), Text(root, "character"),
                    Text(root, "outfit"), Text(root, "app_version"));
            }
            catch (JsonException)
            {
                // A sidecar that will not parse says nothing about the mod. The record beside it is the
                // gate, and it is read next; what this loses is only the display metadata, which is why
                // the confirmation can end up saying "(no author listed)" for a mod that lists one.
                return new Sidecar(null, null, null, null, null, null, null, null);
            }
        }

        private static string? Text(JsonElement root, string field) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text : null;
    }
}
