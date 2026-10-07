using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Blender;
using Remold.Core.Project;

namespace Remold.App.ViewModels;

/// <summary>Where the open mod's round trips live, and how saves from outside editors reach the mod.
///
/// <para>Every file Blender or an image editor is handed sits in an app-owned folder for the mod, never
/// inside the mod folder. So renaming the mod, closing and reopening it, or restarting the app changes
/// nothing for an editor that is still open: its next save lands in the same place, and the mod takes it
/// the moment it is open to take it.</para></summary>
public partial class MainWindowViewModel
{
    /// <summary>The app-owned folders mods' round trips live in.</summary>
    internal RoundTripStore RoundTrips { get; set; } = NewRoundTripStore();

    /// <summary>Where a new window keeps round trips. The test assembly gives each window its own folder,
    /// because windows in one test run are different apps to each other.</summary>
    internal static Func<RoundTripStore> NewRoundTripStore { get; set; } =
        () => new RoundTripStore(RoundTripStore.DefaultRoot);

    private const string PictureRecordFile = "picture.json";

    /// <summary>The open mod's round-trip folder, bound once its mod folder is known.</summary>
    private string? _transportRoot;
    /// <summary>The open mod was given its round-trip id this session and no save has written it yet.</summary>
    private bool _roundTripIdUnsaved;
    /// <summary>Where image-editor save reports are handed to be landed. The window's thread in the app;
    /// tests hand it their own.</summary>
    internal Action<Action> PictureSaveDispatch { get; set; } = OnUi;
    private PictureSaveWatcher? _pictureWatcher;
    private FileSystemWatcher? _arrivals;
    private readonly Dictionary<string, PictureIngress> _pictureRoundTrips =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deferredPictureSaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What an image-editor transport needs to be reopened from its folder: the slot it was opened
    /// for, where its first save landed, what the modder agreed to at Open, and which save the mod last
    /// took, with that file's length and write time so an unchanged file is known without reading it.</summary>
    private sealed record PictureRecord(EditSlotRef Slot, EditSlotRef? LandedSlot, string Label,
        ProjectAssetSource? Source, EditTextureSharing LaunchSharing, int? LaunchUses, bool SharedConsent,
        string LandedIdentity, string RefusedIdentity = "", long LandedLength = 0, long LandedWritten = 0);

    /// <summary>Bind the open mod to its round-trip folder, found by the id the mod carries. A mod with no id
    /// yet, or carrying the id of a mod it was copied from, is given a new one; <see cref="KeepRoundTripId"/>
    /// saves it before the mod first hands a file out.</summary>
    private string BindRoundTrips(string modRoot)
    {
        var session = _projectDocument.Session;
        if (_transportRoot is null)
        {
            string? id = session.RoundTripId;
            string? folder = RoundTripStore.IsId(id) ? RoundTrips.Claim(id!, modRoot) : null;
            if (folder is null)
            {
                id = RoundTripStore.NewId();
                folder = RoundTrips.Claim(id, modRoot)
                    ?? throw new InvalidOperationException("a new round-trip id was already claimed");
                session.SetRoundTripId(id);
                _roundTripIdUnsaved = true;
            }
            _transportRoot = folder;
        }
        session.SetTransportRoot(_transportRoot);
        EnsureArrivalWatch();
        return _transportRoot;
    }

    /// <summary>Save the open mod's round-trip id if this session gave it one, just before a file is handed
    /// to Blender or the image editor: without it, the next run would give the mod another id and never find
    /// that file again. The mod is written the way any save writes it, without the folder rename and form
    /// sync an autosave adds. A mod converted from an older format at open is converted by this write, and
    /// says so as a first save does.</summary>
    /// <returns>False, having said so on <paramref name="status"/>, when the mod could not be saved; the
    /// caller then hands nothing out.</returns>
    private bool KeepRoundTripId(IProgress<string> status, string handedTo)
    {
        if (!_roundTripIdUnsaved) return true;
        try
        {
            bool migrating = _projectDocument.OpenedLegacy;
            PersistProject();
            _roundTripIdUnsaved = false;
            if (migrating) ShowMigrationReport(_projectDocument.LastMigrationReport);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't save the mod's round-trip id", e);
            status.Report(RoundTripIdNotSaved(handedTo));
            return false;
        }
    }

    internal static string RoundTripIdNotSaved(string handedTo) =>
        $"Couldn't save the mod, so {handedTo} wasn't opened. {SaveFailedSteer}";

    private void ForgetRoundTrips()
    {
        _pictureWatcher?.Dispose();
        _pictureWatcher = null;
        _pictureRoundTrips.Clear();
        _deferredPictureSaves.Clear();
        _transportRoot = null;
        _roundTripIdUnsaved = false;
    }

    // ---- image-editor saves ------------------------------------------------

    private void EnsurePictureWatch()
    {
        if (_pictureWatcher is not null || _transportRoot is null) return;
        var owner = _projectDocument;
        _pictureWatcher = new PictureSaveWatcher(_transportRoot, PictureRecordFile,
            folder => PictureSaveDispatch(() => LandPictureSave(owner, folder, resumed: false)),
            failure => PictureSaveDispatch(() => RestartPictureWatch(owner, failure)));
    }

    /// <summary>The operating system stopped reporting changes to the folder. Every save it did not report
    /// is still in its file, so a fresh watch and a scan take all of them.</summary>
    private void RestartPictureWatch(AuthoredProjectDocument owner, Exception failure)
    {
        AppLog.Write("The image-editor save watch restarted", failure);
        if (!ReferenceEquals(owner, _projectDocument)) return;
        _pictureWatcher?.Dispose();
        _pictureWatcher = null;
        try
        {
            EnsurePictureWatch();
            ScanPictureSaves();
        }
        catch (Exception e)
        {
            AppLog.Write("The image-editor save watch couldn't restart", e);
            EditPage.ReportStatus($"Stopped watching for saves from the image editor: {Reason(e)} "
                + "Open the mod again to apply them.");
        }
    }

    /// <summary>Take every save the open mod's image-editor transports hold that the mod has not taken yet:
    /// saves made while the mod was closed or the app was not running, and saves a watch missed.</summary>
    internal void ScanPictureSaves()
    {
        if (_transportRoot is null || !Directory.Exists(_transportRoot)) return;
        foreach (string record in Directory.EnumerateFiles(_transportRoot, PictureRecordFile,
                     SearchOption.AllDirectories).ToList())
            LandPictureSave(_projectDocument, Path.GetDirectoryName(record)!, resumed: true);
    }

    /// <summary>Retry the saves a scan found while their subject was still being read.</summary>
    private void RetryDeferredPictureSaves()
    {
        if (_deferredPictureSaves.Count == 0) return;
        var folders = _deferredPictureSaves.ToList();
        _deferredPictureSaves.Clear();
        foreach (string folder in folders) LandPictureSave(_projectDocument, folder, resumed: true);
    }

    /// <summary>Land the save waiting in one transport folder, if there is one the mod has not taken. UI
    /// thread.</summary>
    /// <param name="resumed">The save was found by a scan rather than reported as it happened. Such a save
    /// waits for its subject's read instead of being refused for it, because the modder did not just save
    /// and has nothing to retry.</param>
    internal void LandPictureSave(AuthoredProjectDocument owner, string folder, bool resumed)
    {
        // Raised from a watcher and a scan, where nothing above would report a failure, so every failure is
        // said here.
        try { TryLandPictureSave(owner, folder, resumed); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write("Couldn't apply an image editor's save", e);
            EditPage.ReportStatus($"Couldn't apply the image editor's save: {Reason(e)}");
        }
    }

    private void TryLandPictureSave(AuthoredProjectDocument owner, string folder, bool resumed)
    {
        folder = Path.GetFullPath(folder);
        // A save reported for a mod that closed before it was handled. It stays in the editor's file, and
        // that mod takes it when it opens.
        if (!ReferenceEquals(owner, _projectDocument))
        {
            EditPage.ReportStatus(PictureSaveModClosed(ClosedModName(owner)));
            return;
        }
        if (PictureRoundTripIn(folder) is not { } ingress) return;
        var status = ingress.Status ?? PageStatus;
        // Read before the file's contents, so a save made while they are read changes it and is looked at
        // again rather than taken for the one already landed.
        var stamp = PictureStamp(ingress.Outbound);
        if (stamp is not null && stamp == ingress.LandedStamp) return;
        string saved;
        try { saved = PictureIdentity(ingress.Outbound); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            status.Report($"Couldn't apply the image editor's save: {Reason(e)}");
            return;
        }
        if (string.Equals(saved, ingress.LandedIdentity, StringComparison.Ordinal))
        {
            // The same picture under a new write time: remember the time, so the next open skips it unread.
            if (stamp != ingress.LandedStamp)
            {
                ingress.LandedStamp = stamp;
                TryUpdatePictureRecord(ingress);
            }
            return;
        }
        if (resumed && TextureSharingAt(LiveSlotFor(ingress)).Kind == EditTextureSharing.Unknown)
        {
            _deferredPictureSaves.Add(folder);
            return;
        }
        // A save already refused, found again by a later open, is retried without saying the same refusal
        // twice: it was said when that save was made. If the retry lands, that is said as usual.
        bool alreadySaid = resumed && string.Equals(saved, ingress.RefusedIdentity, StringComparison.Ordinal);
        // A save into an existing edit's slot through its own transport writes the landed picture back over
        // the editor's file; the other routes leave the file exactly as the editor saved it.
        bool writesBack = !IsBareCard(ingress.Slot) && ingress.LandedSlot is null;
        if (!PublishPictureReturn(ingress, status, alreadySaid ? new ReportTo(_ => { }) : null))
        {
            if (alreadySaid) return;
            ingress.RefusedIdentity = saved;
            TryUpdatePictureRecord(ingress);
            return;
        }
        if (writesBack)
        {
            ingress.LandedStamp = PictureStamp(ingress.Outbound);
            ingress.LandedIdentity = TryPictureIdentity(ingress.Outbound) ?? saved;
        }
        else
        {
            ingress.LandedStamp = stamp;
            ingress.LandedIdentity = saved;
        }
        ingress.RefusedIdentity = "";
        TryUpdatePictureRecord(ingress);
    }

    /// <summary>Record a transport Open just lent to the image editor, so its saves land from now on,
    /// including after the mod is closed and reopened or the app restarts.</summary>
    /// <remarks>Throws when the record cannot be written: an Open whose saves could never be found is not
    /// launched.</remarks>
    private void TrackPictureRoundTrip(PictureIngress ingress, IProgress<string> status)
    {
        ingress.Status = status;
        ingress.LandedStamp = PictureStamp(ingress.Outbound);
        ingress.LandedIdentity = PictureIdentity(ingress.Outbound);
        // A card with no edit yet has no slot in the mod to check a reopen against; its first save makes one.
        if (!IsBareCard(ingress.Slot)) ProjectAssetIngress.RecordLent(ingress.Session!);
        SavePictureRecord(ingress);
        _pictureRoundTrips[Path.GetFullPath(ingress.Folder)] = ingress;
        EnsurePictureWatch();
    }

    private void UntrackPictureRoundTrip(PictureIngress ingress)
    {
        _pictureRoundTrips.Remove(Path.GetFullPath(ingress.Folder));
        try { File.Delete(Path.Combine(ingress.Folder, PictureRecordFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't remove an image-editor transport record", e);
        }
    }

    private PictureIngress? PictureRoundTripIn(string folder)
    {
        if (_pictureRoundTrips.TryGetValue(folder, out var known)) return known;
        string file = Path.Combine(folder, PictureRecordFile);
        if (!File.Exists(file)) return null;
        PictureRecord? record;
        try { record = JsonSerializer.Deserialize<PictureRecord>(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write("Couldn't read an image-editor transport record", e);
            record = null;
        }
        string outbound = Path.Combine(folder, PictureSaveWatcher.SavedFile);
        if (record is null || !File.Exists(outbound))
        {
            EditPage.ReportStatus(PictureRoundTripUnreadable);
            return null;
        }
        var ingress = new PictureIngress(record.Slot, EditSession, outbound, record.Label, record.Source,
            record.LaunchSharing, record.LaunchUses, record.SharedConsent)
        {
            LandedSlot = record.LandedSlot,
            LandedIdentity = record.LandedIdentity,
            RefusedIdentity = record.RefusedIdentity,
            LandedStamp = record.LandedLength == 0 && record.LandedWritten == 0
                ? null : (record.LandedLength, record.LandedWritten),
        };
        _pictureRoundTrips[folder] = ingress;
        return ingress;
    }

    internal const string PictureRoundTripUnreadable =
        "Couldn't apply the image editor's save: the app no longer knows which map it belongs to. "
        + "Use Open on the card again.";

    private static void SavePictureRecord(PictureIngress ingress) =>
        RoundTripStore.WriteAtomically(Path.Combine(ingress.Folder, PictureRecordFile),
            JsonSerializer.Serialize(new PictureRecord(ingress.Slot, ingress.LandedSlot, ingress.Label,
                ingress.Source, ingress.LaunchSharing, ingress.LaunchUses, ingress.SharedConsent,
                ingress.LandedIdentity, ingress.RefusedIdentity, ingress.LandedStamp?.Length ?? 0,
                ingress.LandedStamp?.Written ?? 0)));

    /// <summary>Bring a landed or refused transport's record up to date. A failure is logged and nothing
    /// more: the record still names the right map, so the next open only looks at a save it has already
    /// taken, finds nothing new, or repeats a refusal once.</summary>
    private static void TryUpdatePictureRecord(PictureIngress ingress)
    {
        try { SavePictureRecord(ingress); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't update an image-editor transport record", e);
        }
    }

    private static (long Length, long Written)? PictureStamp(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string PictureIdentity(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? TryPictureIdentity(string file)
    {
        try { return PictureIdentity(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private IProgress<string> PageStatus => new ReportTo(line => EditPage.ReportStatus(line));

    private sealed class ReportTo(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    // ---- saves for a mod that is not open ------------------------------------

    /// <summary>Watch every mod's round-trip folder for a save meant for a mod that is not the open one,
    /// and say so as it happens. Nothing is taken: the save waits in its folder, and that mod's open takes
    /// it. Armed with the first mod the app binds and kept for the life of the app.</summary>
    private void EnsureArrivalWatch()
    {
        if (_arrivals is not null) return;
        Directory.CreateDirectory(RoundTrips.Root);
        var watcher = new FileSystemWatcher(RoundTrips.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            // Blender writes whole texture sets here; the default buffer overflows on one open.
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, e) => OnArrival(e.FullPath);
        watcher.Created += (_, e) => OnArrival(e.FullPath);
        watcher.Renamed += (_, e) => OnArrival(e.FullPath);
        // The operating system stopped reporting changes: start a fresh watch. The saves themselves are not at
        // stake, since each mod takes its own when it opens; what a fresh watch keeps is the heads-up.
        watcher.Error += (_, e) => _pageDispatch(() =>
        {
            AppLog.Write("The round-trip arrival watch restarted", e.GetException());
            if (!ReferenceEquals(_arrivals, watcher)) return;
            watcher.Dispose();
            _arrivals = null;
            try { EnsureArrivalWatch(); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("The round-trip arrival watch couldn't restart", failure);
                EditPage.ReportStatus("Stopped watching for saves meant for other mods. "
                    + "Their changes are still applied when you open them.");
            }
        });
        watcher.EnableRaisingEvents = true;
        _arrivals = watcher;
    }

    private readonly Dictionary<string, DateTime> _arrivalReported = new(StringComparer.OrdinalIgnoreCase);

    private void OnArrival(string path)
    {
        string name = Path.GetFileName(path);
        bool blender = name.EndsWith(BlenderBridge.SidecarSuffix, StringComparison.OrdinalIgnoreCase);
        bool picture = name.StartsWith("outbound.", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, PictureRecordFile));
        if (!blender && !picture) return;
        if (RoundTrips.FolderContaining(path) is not { } folder) return;
        _pageDispatch(() =>
        {
            if (string.Equals(folder, _transportRoot, StringComparison.OrdinalIgnoreCase)) return;
            string? modRoot = RoundTrips.OwnerOf(folder);
            // One editor save raises several changes; one line per mod and kind is the news.
            string key = folder + (blender ? "|blender" : "|picture");
            var now = DateTime.UtcNow;
            if (_arrivalReported.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(5))
                return;
            _arrivalReported[key] = now;
            if (modRoot is null)
                ReportArrival(blender ? BlenderSendUnplaced : PictureSaveUnplaced);
            else if (!File.Exists(ModProject.ManifestPathFor(modRoot)))
                ReportArrival(blender ? BlenderSendForMissingMod : PictureSaveForMissingMod);
            else
                ReportArrival(blender ? BlenderReturnModClosed(ModNameAt(modRoot))
                    : PictureSaveModClosed(ModNameAt(modRoot)));
        });
    }

    /// <summary>Say a save meant for another mod where the modder is looking: the Edit page's line while a
    /// mod is open, the notice cell on the Home screen, where that line is not shown. The notice goes when
    /// the next mod opens.</summary>
    private void ReportArrival(string line)
    {
        if (ShowHome)
            MergeNoticeIntoCell(new NoticeMessage(ArrivalNoticeId, "Save waiting", line, ProjectScoped: true,
                Severity: NoticeSeverity.Info));
        else
            EditPage.ReportStatus(line);
    }

    internal const string ArrivalNoticeId = "round-trip.arrival";

    /// <summary>What a save for a mod that is no longer where the app last saw it says. A mod moved outside
    /// the app takes its round trips with it the next time it is opened from its new folder.</summary>
    internal const string PictureSaveForMissingMod =
        "The image editor saved a picture for a mod that was moved or deleted. "
        + "If it was moved, open it from its new folder to apply the picture.";

    internal const string BlenderSendForMissingMod =
        "Blender sent back changes for a mod that was moved or deleted. "
        + "If it was moved, open it from its new folder to apply them.";

    /// <summary>What a save into a round-trip folder that no longer names its mod says: the folder was
    /// cleared after a week unused and the editor, still open, wrote into it again.</summary>
    internal const string BlenderSendUnplaced =
        "Blender sent back changes the app can no longer place. Open the part in Blender again.";

    internal const string PictureSaveUnplaced =
        "The image editor saved a picture the app can no longer place. Use Open on the card again.";

    /// <summary>The name a mod that is not open goes by, read from its own manifest: the same name
    /// <see cref="ClosedModName(AuthoredProjectDocument)"/> gives a mod the app has just closed.</summary>
    private static string ModNameAt(string modRoot)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(ModProject.ManifestPathFor(modRoot)));
            if (manifest.RootElement.TryGetProperty("info", out var info)
                && info.TryGetProperty("name", out var name)
                && name.GetString()?.Trim() is { Length: > 0 } named)
                return named;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException)
        {
            // A mod whose manifest cannot be read is still named, the way the app names an unnamed one.
        }
        return UntitledMod;
    }

    // ---- the mod folder --------------------------------------------------------

    internal const string FolderNotRenamedNoticeId = "project.folder-not-renamed";

    /// <summary>The folder name the last failed rename wanted, so a failure is logged once rather than on
    /// every autosave that retries it.</summary>
    private string? _folderRenameFailedFor;

    internal static NoticeMessage FolderNotRenamedNotice(string wanted, string kept) =>
        new(FolderNotRenamedNoticeId, "Folder not renamed",
            $"Couldn't rename the mod folder to '{wanted}'. A file in it may be open in another program, "
            + "or the folder may be read-only. Close any program using a file in the folder, or make the "
            + $"folder writable. The mod is saved in '{kept}'. The next save tries again.",
            ProjectScoped: true);

    /// <summary>What the Blender send a pre-update Blender left in the mod folder says.</summary>
    internal const string LegacyBlenderSendNoticeId = "project.legacy-blender-send";

    internal static NoticeMessage LegacyBlenderSendNotice() =>
        new(LegacyBlenderSendNoticeId, "Blender changes not applied",
            "Couldn't apply the file sent back from Blender: it came from a Blender window opened before "
            + "this update. Open the part in Blender again.",
            ProjectScoped: true);

    /// <summary>Clear out the round-trip folder older versions kept inside the mod. A Blender send one left
    /// there cannot land, because its session belongs to the old layout, so it is named before the folder
    /// goes.</summary>
    private void ClearLegacyRoundTrips(string modRoot)
    {
        string legacy = Path.Combine(modRoot, ProjectAssetIngress.LegacyDirectoryName);
        if (!Directory.Exists(legacy)) return;
        try
        {
            if (Directory.EnumerateFiles(legacy, "*" + BlenderBridge.SidecarSuffix,
                    SearchOption.AllDirectories).Any())
                MergeNoticeIntoCell(LegacyBlenderSendNotice());
            Directory.Delete(legacy, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only old scratch copies are left behind; the next open tries again.
            AppLog.Write("Couldn't remove the mod's old round-trip folder", e);
        }
    }
}
