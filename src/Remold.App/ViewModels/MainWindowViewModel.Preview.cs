using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.App.Views;
using Remold.Core.Textures;

namespace Remold.App.ViewModels;

/// <summary>
/// The mod's preview image: the one picture that ships with the build (<c>gf2mod.json</c>'s
/// <c>preview</c>) and stands in for the mod on the home screen's recent rows.
///
/// <para>The image is always COPIED into the project folder under a fixed name, and
/// <see cref="Remold.Core.Project.ProjectInfo.Preview"/> holds that workspace-relative name — a project
/// carries its own preview, never a reference to a file somewhere else on disk that a later move would
/// break.</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The extensions the control takes, lowercase. The picker's patterns are derived from this
    /// list, so the drop gate and the dialog filter can't accept different sets.</summary>
    public static readonly string[] PreviewExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    /// <summary>The same set as picker patterns.</summary>
    public static string[] PreviewPatterns => PreviewExtensions.Select(e => "*" + e).ToArray();

    /// <summary>The project-local copy's name, minus the extension it takes from the source. One name, so a
    /// project can only ever hold one preview.</summary>
    private const string PreviewStem = "preview";

    /// <summary>Decode width for the pane's preview, and the CEILING on it rather than the flat answer — see
    /// <see cref="PreviewDecodeWidth"/>. ONE decode serves both renders: the slot draws it at tile size and
    /// the hover tip draws it at up to this width, so a second decode for the zoom would be the same bytes
    /// read twice. Wide enough for the tip, and still nowhere near a 4K source's full size.</summary>
    internal const int PreviewThumbWidth = 480;

    /// <summary>What to actually ask the decoder for, given the source's own width off its header.
    /// <c>Bitmap.DecodeToWidth</c> has no natural-size clamp: a source NARROWER than the width asked for is
    /// scaled UP to it, and the hover tip then draws that stretch at full size — a 64×64 icon would hang in
    /// the flyout at 360×360, blurred, claiming a detail the file never had. Capped at the source's own
    /// width, a small image stays small and the tip shows it at 1:1.
    ///
    /// <para>An unreadable header (null) falls back to the full width, which is exactly what the decode did
    /// before this cap. A width of zero or less is refused the same way rather than passed on: it comes from
    /// a header this app didn't write, and asking the decoder for a zero-wide bitmap is a worse answer than
    /// asking for the one that always worked.</para></summary>
    internal static int PreviewDecodeWidth(int? naturalWidth) =>
        naturalWidth is { } w && w > 0 && w < PreviewThumbWidth ? w : PreviewThumbWidth;

    /// <summary>The decoded thumbnail, or null when there is none (nothing set, the file is gone, or it
    /// wouldn't decode). Owned here: replaced and cleared through <see cref="SetPreviewImage"/>.</summary>
    [ObservableProperty] private Bitmap? _previewImage;

    /// <summary>A preview is set and its file is there.</summary>
    [ObservableProperty] private bool _hasPreview;

    /// <summary>A preview is set and its file is NOT there — deleted from the project folder behind the
    /// app's back. Its own state: the build says so, and the control offers the two ways out.</summary>
    [ObservableProperty] private bool _previewMissing;

    /// <summary>Neither set nor missing — the empty state that offers the drop target.</summary>
    public bool HasNoPreview => !HasPreview && !PreviewMissing;

    /// <summary>A decode is out on the pool for the file the state currently names. Its one job is to tell a
    /// slot still waiting from a slot whose file will never decode.</summary>
    [ObservableProperty] private bool _previewDecoding;

    /// <summary>What the thumbnail says on hover: the file the project carries, and the image's own pixel
    /// size once the header read lands ("preview.png · 1920×1080"). Empty when there is nothing set.</summary>
    [ObservableProperty] private string _previewThumbTip = "";

    /// <summary>The file is there and it would not decode — a corrupt or truncated image, or a format the
    /// decoder doesn't take under an extension it does. Its own visible state: the box shows the app's
    /// no-preview tile rather than a silent empty hole beside two live buttons.</summary>
    public bool PreviewUndecodable => HasPreview && !PreviewDecoding && PreviewImage is null;

    partial void OnHasPreviewChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoPreview));
        OnPropertyChanged(nameof(PreviewUndecodable));
    }
    partial void OnPreviewMissingChanged(bool value) => OnPropertyChanged(nameof(HasNoPreview));
    partial void OnPreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(PreviewUndecodable));
    partial void OnPreviewDecodingChanged(bool value) => OnPropertyChanged(nameof(PreviewUndecodable));

    /// <summary>The control needs a folder to copy INTO, and a mod that has never been saved has none.</summary>
    public bool PreviewEnabled => _project.RootDir is not null;

    /// <summary>Why the control can't take an image yet, naming the route out — the app's remedies say which
    /// menu to use, not just what has to happen.</summary>
    public const string PreviewNeedsSave = "Use File · Save Mod first.";

    /// <summary>What the slot says when it CAN take one: both ways in, then what the image is for. Nothing
    /// else on the pane says a drop works.</summary>
    public const string PreviewPickReady = "Drop an image or click to choose. Ships with the mod.";

    /// <summary>What the slot says on hover. A run in flight outranks every other reason, as the Build and
    /// Install gates' tooltips do: the slot is off under a build, and answering "ships with the mod" beneath
    /// a dead control explains the wrong thing.</summary>
    public string PreviewPickTip =>
        IsModBuilding ? BuildRunningReason
        : !PreviewEnabled ? PreviewNeedsSave
        : PreviewPickReady;

    /// <summary>The missing state's title: the file the project actually names, so the modder knows which
    /// one to put back. The fallback is unreachable through the missing state (which needs a set field) but
    /// the binding is read whether or not the block is on screen.</summary>
    public string PreviewMissingTitle
    {
        get
        {
            var rel = _project.Info.Preview;
            return string.IsNullOrWhiteSpace(rel) ? "Preview image missing" : rel + " missing";
        }
    }

    /// <summary>A drop of anything but one image file. Refused on the pane's own line, with nothing
    /// changed.</summary>
    public const string PreviewNotAnImage = "Not an image file. Use .png, .jpg, .bmp or .webp.";
    public const string PreviewOneAtATime = "One image at a time.";

    /// <summary>A drag carrying no local file at all — a browser image, a zip entry, a mail attachment. The
    /// payload never became a file, so "one at a time" would answer a question nobody asked.</summary>
    public const string PreviewNoFileInDrop = "No file in the drop. Save the image as a file first.";

    /// <summary>The build's line for a preview whose file went missing. The sidecar skips a preview it
    /// can't find, so the mod still builds — what it can't do is carry the image.</summary>
    public const string PreviewMissingWarning = "Preview image missing. This build ships without one";

    /// <summary>Whether <paramref name="path"/> is one of the extensions the control takes. Extension only:
    /// the build copies the bytes and the mod manager decodes them, so nothing here needs to read the
    /// image.</summary>
    internal static bool IsPreviewImage(string path) =>
        PreviewExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>The absolute path of the preview on disk, or null when there is no usable one. The single
    /// read of that question — the control's state, the build's warning and the signature all go through
    /// it, so they can't disagree. <paramref name="set"/> reports whether the FIELD names one at all, which
    /// is what tells "none" from "missing".</summary>
    private string? ResolvedPreviewPath(out bool set)
    {
        var rel = _project.Info.Preview;
        set = !string.IsNullOrWhiteSpace(rel);
        if (!set || _project.RootDir is null) return null;
        try
        {
            var path = _project.Resolve(rel!);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>The preview as a BUILD reads it: the name the sidecar would carry, plus the file's own
    /// stamp. A replacement under the same extension keeps the name, so without the stamp the result bar
    /// would keep reading clean over an image the built folder no longer holds.</summary>
    private string PreviewSignature()
    {
        var rel = _project.Info.Preview;
        if (string.IsNullOrWhiteSpace(rel)) return "";
        try
        {
            var f = new FileInfo(_project.Resolve(rel));
            return f.Exists ? $"{rel}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : rel;
        }
        catch { return rel; }
    }

    /// <summary>The build warning a missing preview carries, else null. Read off DISK rather than off
    /// <see cref="PreviewMissing"/>: a file deleted while the pane sat open would otherwise let a build
    /// drop the image with nothing said.</summary>
    private string? MissingPreviewWarning()
    {
        var path = ResolvedPreviewPath(out bool set);
        return set && path is null ? PreviewMissingWarning : null;
    }

    /// <summary>Re-read the control off the project: which of the three states it shows, and the thumbnail
    /// for it. Cheap enough for every entry into the step.</summary>
    internal void RefreshPreviewState()
    {
        OnPropertyChanged(nameof(PreviewEnabled));
        OnPropertyChanged(nameof(PreviewPickTip));
        OnPropertyChanged(nameof(PreviewMissingTitle));
        var path = ResolvedPreviewPath(out bool set);
        HasPreview = path is not null;
        PreviewMissing = set && path is null;
        ShowWarnings();   // the missing-preview row is a live warning, not a run's
        LoadPreviewImage(path);
    }

    /// <summary>Drop the control's state without reading the project — the teardown between mods runs
    /// BEFORE the new project is in place, so there is nothing to read yet.</summary>
    private void ClearPreviewState()
    {
        _previewImageGeneration++;   // any decode in flight belongs to the mod being left
        _previewImageKey = null;
        PreviewThumbTip = "";
        SetPreviewImage(null, _previewImageGeneration);
        HasPreview = false;
        PreviewMissing = false;
        OnPropertyChanged(nameof(PreviewEnabled));
        OnPropertyChanged(nameof(PreviewPickTip));
        OnPropertyChanged(nameof(PreviewMissingTitle));
    }

    /// <summary>The build flag moved: the slot's tooltip answers with the busy sentence while a run holds
    /// the pane, exactly as the change list's tips do. Raised from
    /// <see cref="OnIsModBuildingChanged"/>, where the pane's other build-state tips are raised.</summary>
    private void RaisePreviewBuildState() => OnPropertyChanged(nameof(PreviewPickTip));

    /// <summary>Rejects an out-of-order decode, which is then disposed rather than left to the finalizer.</summary>
    private int _previewImageGeneration;

    /// <summary>What the thumbnail on screen was decoded from — the path plus the file's stamp, so a
    /// replacement under the same name still re-decodes while a routine re-read does not.</summary>
    private string? _previewImageKey;

    private void LoadPreviewImage(string? path)
    {
        string? key = null;
        if (path is not null)
        {
            try { var f = new FileInfo(path); key = $"{path}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; }
            catch { key = path; }
        }
        // A routine re-read (entering the step, a save) must not re-decode the same file. A decode that
        // FAILED leaves no bitmap under a non-null key, which is what lets a re-entry retry it.
        if (key == _previewImageKey && (PreviewImage is not null) == (key is not null)) return;
        _previewImageKey = key;

        int generation = ++_previewImageGeneration;
        if (path is null) { PreviewThumbTip = ""; SetPreviewImage(null, generation); return; }
        // Held until this decode settles, so the empty box reads as "still working" rather than as the
        // undecodable state it becomes only when a decode has come back with nothing.
        PreviewDecoding = true;
        var relative = _project.Info.Preview ?? Path.GetFileName(path);
        PreviewThumbTip = relative;   // the name straight away; the size joins it when the read comes back
        Task.Run(() =>
        {
            // The source's own header, read ONCE and used twice: it caps the decode below, and it carries
            // the tip's size line — which used to open the file for itself, so this is the same one read
            // the tip already cost, moved ahead of the decode that now needs it too.
            var natural = PngInfo.TrySize(path);
            Bitmap? bmp = null;
            // A file that won't open or won't decode leaves the slot empty; the state stays "has preview"
            // because the file IS there, and Replace/Remove are both still on the row.
            try
            {
                // Shared for write AND delete: a Replace or Remove landing while this decode is still
                // reading must not fail on the handle it holds. A decode that reads bytes being overwritten
                // is discarded by the generation the replacement bumps.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                bmp = Bitmap.DecodeToWidth(fs, PreviewDecodeWidth(natural?.Width));
            }
            catch { bmp = null; }
            var tip = PreviewTip(relative, natural, decoded: bmp is not null);
            Dispatcher.UIThread.Post(() =>
            {
                bool mine = generation == _previewImageGeneration;
                SetPreviewImage(bmp, generation);
                if (mine) PreviewThumbTip = tip;
            });
        });
    }

    /// <summary>What the thumbnail says on hover, from the size already read off the SOURCE file's header —
    /// never from the bitmap on screen. That one is capped at <see cref="PreviewThumbWidth"/>, so anything
    /// wider than the cap is scaled DOWN to it and reporting its dimensions would hand the modder the app's
    /// decode size and call it the image's. Offered only for a file that actually decoded — the identify step
    /// will put a size on bytes no decoder can render, and a claimed "1920×1080" under a "no preview" tile is
    /// worse than a bare name.</summary>
    internal static string PreviewTip(string relative, (int Width, int Height)? size, bool decoded) =>
        decoded && size is { } s ? $"{relative} · {s.Width}×{s.Height}" : relative;

    /// <summary>The same line for a caller holding only the path. The decode path reads the header for its
    /// own cap and passes the result to the overload above, so this one exists for callers with no size in
    /// hand rather than to read the file a second time.</summary>
    internal static string PreviewTip(string relative, string path, bool decoded) =>
        PreviewTip(relative, decoded ? PngInfo.TrySize(path) : null, decoded);

    /// <summary>Settle the thumbnail on the UI thread. Disposes what it replaces, and disposes a superseded
    /// decode rather than letting it land.</summary>
    private void SetPreviewImage(Bitmap? bmp, int generation)
    {
        if (generation != _previewImageGeneration) { bmp?.Dispose(); return; }
        if (!ReferenceEquals(PreviewImage, bmp)) PreviewImage?.Dispose();
        PreviewImage = bmp;
        PreviewDecoding = false;   // this generation's decode has settled, bitmap or not
    }

    /// <summary>A drop on the preview slot. Exactly one file, and an image: everything else is refused on
    /// the pane's own line with nothing changed, and each refusal says which thing was wrong — a payload
    /// that carried no file at all is a different answer from too many of them.</summary>
    public void DropPreview(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { Footer = Footer.Notice(PreviewNoFileInDrop); return; }
        if (paths.Count != 1) { Footer = Footer.Notice(PreviewOneAtATime); return; }
        SetPreviewFrom(paths[0]);
    }

    /// <summary>Take <paramref name="sourcePath"/> as this mod's preview. The drop and the picker both land
    /// here, so the two ways in can't diverge.</summary>
    public void SetPreviewFrom(string sourcePath)
    {
        if (IsModBuilding) { Footer = Footer.Notice(BuildRunningReason); return; }
        if (!PreviewEnabled) { Footer = Footer.Notice(PreviewNeedsSave); return; }
        if (!IsPreviewImage(sourcePath)) { Footer = Footer.Notice(PreviewNotAnImage); return; }

        var relative = PreviewStem + Path.GetExtension(sourcePath).ToLowerInvariant();
        // Written beside the destination and moved onto it, so a copy that dies mid-stream can't leave a
        // truncated file where the healthy one was: File.Copy over the destination empties it at the START.
        string? staged = null;
        try
        {
            var dest = _project.Resolve(relative);
            // The NEW copy lands before the old one is dropped: a failed copy must never leave the field
            // naming a file that has already been deleted.
            if (!string.Equals(Path.GetFullPath(sourcePath), dest, StringComparison.OrdinalIgnoreCase))
            {
                // Only an OVERWRITE needs staging: a copy straight onto the destination empties it at the
                // start, so a failure mid-stream would leave a truncated preview where a healthy one was.
                // With nothing there to lose, the plain copy is the whole operation.
                if (File.Exists(dest))
                {
                    staged = dest + ".tmp";
                    File.Copy(sourcePath, staged, overwrite: true);
                    // Replace, not Move-with-overwrite: the pane's OWN decode of the preview being replaced
                    // can still hold the destination open, and MoveFileEx refuses that. This is the route
                    // the manifest's atomic write already takes, for the same reason.
                    File.Replace(staged, dest, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    staged = null;   // the swap consumed it
                }
                else
                {
                    File.Copy(sourcePath, dest);
                }
            }
            DeleteProjectPreview(keepRelative: relative);
        }
        catch (Exception e)
        {
            if (staged is not null) { try { File.Delete(staged); } catch { } }
            // Re-read before the line: the control and the result bar were reporting the state this attempt
            // was going to replace, and a failed attempt can still have moved what is on disk.
            RefreshPreviewState();
            RefreshBuildResultStale();
            Footer = Footer.Notice($"Couldn't copy the image. {e.Message}");
            return;
        }

        _project.Info.Preview = relative;
        PersistPreviewChange();
    }

    public const string PreviewRemoveTitle = "Remove preview image";
    /// <summary>The confirm's question. Remove deletes the mod's copy and there is no undo, so the gesture
    /// is asked before it runs.</summary>
    public const string PreviewRemoveQuestion = "Remove the preview image?";
    /// <summary>The consequence line when nothing on disk goes: the field named a file this app did not
    /// write, or the file is already gone.</summary>
    public const string PreviewRemoveKeepsFiles = "The mod's files are not touched.";
    /// <summary>The consequence line when the mod's own copy goes with the field.</summary>
    internal static string PreviewRemoveDeletes(string relative) =>
        $"{relative} is deleted from the mod's folder.";

    /// <summary>Whether Remove would actually delete a file: the field names this app's own copy AND that
    /// copy is there. The confirm reads it rather than promising a delete in the two states where the
    /// mod's files stay exactly as they are.</summary>
    private bool RemoveWouldDeleteFile(out string relative)
    {
        relative = _project.Info.Preview ?? "";
        return relative.Length > 0 && IsOwnPreviewName(relative) && ResolvedPreviewPath(out _) is not null;
    }

    /// <summary>What the confirm asks, and what it says will happen.</summary>
    internal string PreviewRemoveBody =>
        PreviewRemoveQuestion + "\n\n"
        + (RemoveWouldDeleteFile(out var rel) ? PreviewRemoveDeletes(rel) : PreviewRemoveKeepsFiles);

    /// <summary>The Remove confirm, behind a seam: the dialog needs a window, and a headless host has none.
    /// Null = the real dialog, which is every run of the app.</summary>
    internal Func<Task<bool>>? PreviewRemoveConfirm;

    /// <summary>Ask before the delete. A host with no window has nothing to ask WITH, and refusing there
    /// would turn Remove into a silent no-op rather than a confirmed one; the seam above is how a headless
    /// caller states its answer.</summary>
    private async Task<bool> ConfirmRemovePreviewAsync()
    {
        if (PreviewRemoveConfirm is { } ask) return await ask();
        if (MainWindow is not { } owner) return true;
        return await ConfirmWindow.Show(owner, PreviewRemoveTitle, PreviewRemoveBody,
            "Remove", "Cancel", danger: true);
    }

    /// <summary>Clear the preview and delete the project's copy of it, after the confirm. The busy and
    /// unsaved gates run BEFORE the question: nothing is asked that would then be refused.</summary>
    [RelayCommand]
    private async Task RemovePreview()
    {
        if (IsModBuilding) { Footer = Footer.Notice(BuildRunningReason); return; }
        if (!PreviewEnabled) { Footer = Footer.Notice(PreviewNeedsSave); return; }
        if (!await ConfirmRemovePreviewAsync()) return;   // cancelled: nothing changes, nothing is said
        DeleteProjectPreview(keepRelative: null);
        _project.Info.Preview = null;
        PersistPreviewChange();
    }

    /// <summary>Whether <paramref name="relative"/> is a name only this app writes — the fixed stem under one
    /// of the taken extensions. Anything else in the field came from a hand-edited or shared manifest and
    /// names a file the app did not make, which is not its to delete.</summary>
    private static bool IsOwnPreviewName(string relative) =>
        PreviewExtensions.Any(ext =>
            string.Equals(relative, PreviewStem + ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>Delete the project-local copy the field currently names, unless it is the one just written.
    /// Confined twice: under the project folder, AND under the one name this app itself writes. A manifest
    /// naming a project file of its own ("targets/replace_body.glb") keeps that file — Replace and Remove
    /// clear the field and leave it, rather than deleting an asset the mod is built from.</summary>
    private void DeleteProjectPreview(string? keepRelative)
    {
        var rel = _project.Info.Preview;
        if (string.IsNullOrWhiteSpace(rel) || _project.RootDir is null) return;
        if (keepRelative is not null && string.Equals(rel, keepRelative, StringComparison.OrdinalIgnoreCase))
            return;
        if (!IsOwnPreviewName(rel!)) return;
        try
        {
            var path = _project.Resolve(rel);
            var root = Path.GetFullPath(_project.RootDir);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            File.Delete(path);
        }
        // A copy that won't delete is left behind unreferenced: the sidecar carries only the file the field
        // names, so nothing ships it and nothing reads it.
        catch { }
    }

    /// <summary>What every preview edit lands. The field is part of what a build ships, so it takes the same
    /// autosave-then-report order a change tick uses, and the result bar re-reads whether the folder it
    /// names still matches.</summary>
    private void PersistPreviewChange()
    {
        var autosaveFailure = TryAutoSaveProject();
        RefreshPreviewState();
        RefreshBuildResultStale();
        Footer = autosaveFailure is null ? Footer.Ticked(CurrentCounts()) : Footer.Notice(autosaveFailure);
    }

    // ---- home screen: the recent rows' thumbnails ---------------------------

    /// <summary>Decode width for a recent row's thumbnail. The slot is tiny; nothing on that screen wants a
    /// full-size image in memory.</summary>
    private const int RecentThumbWidth = 128;

    /// <summary>A fill whose stamp is stale drops its rows rather than settling a bitmap onto a list that
    /// has since been replaced.</summary>
    private int _recentThumbGeneration;

    /// <summary>Arriving at the home screen: the rows are on screen now, so the fill a refresh behind the
    /// flow skipped runs here.</summary>
    partial void OnShowHomeChanged(bool value)
    {
        if (value) FillRecentThumbs();
    }

    /// <summary>Fill the recent rows' previews off the UI thread: one cheap manifest read per row, then a
    /// small decode. EVERY failure — a manifest that won't load, a preview field naming a file that isn't
    /// there, an image that won't decode — leaves the row exactly as a row with no preview, CLEARING
    /// whatever an earlier fill settled onto it. The home screen reports on mods it has not opened, so
    /// nothing about them is an error worth a surface.</summary>
    private void FillRecentThumbs()
    {
        int generation = ++_recentThumbGeneration;
        // Only while the rows are actually on screen. The recent list is rebuilt by every autosave, and
        // this is a manifest read plus a decode per row — work worth nothing behind the flow. Coming back
        // to the home screen runs it.
        if (!ShowHome) return;
        var rows = RecentMods.ToList();
        if (rows.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (generation != _recentThumbGeneration) return;
                var bmp = TryLoadRecentThumb(row.Path);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _recentThumbGeneration) { bmp?.Dispose(); return; }
                    // A null is a CLEAR, not a skip: the rows outlive the fill that filled them, so a mod
                    // whose preview has since gone would otherwise keep showing the one an earlier pass
                    // settled onto its row.
                    if (bmp is null) row.DisposeThumb(); else row.SetThumb(bmp);
                });
            }
        });
    }

    /// <summary>The preview file of the project at <paramref name="projectPath"/>, or null when it has none,
    /// the manifest won't load, or the file it names isn't there. The MANIFEST is all this reads — the home
    /// screen reports on mods it has not opened and never builds their workspace state.</summary>
    internal static string? RecentPreviewPath(string projectPath)
    {
        try
        {
            var project = Remold.Core.Project.ModProject.Load(projectPath);
            var rel = project.Info.Preview;
            if (string.IsNullOrWhiteSpace(rel)) return null;
            var path = project.Resolve(rel);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>One recent mod's preview, decoded small, or null when it has none or anything at all goes
    /// wrong.</summary>
    private static Bitmap? TryLoadRecentThumb(string projectPath)
    {
        if (RecentPreviewPath(projectPath) is not { } path) return null;
        try
        {
            // shared for write and delete, as the pane's own decode is: the mod may be open elsewhere
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Bitmap.DecodeToWidth(fs, RecentThumbWidth);
        }
        catch { return null; }
    }

    /// <summary>Release every bitmap this window owns. Called as the window closes: the rows outlive nothing,
    /// and a native bitmap left to a finalizer is a handle held past the process's useful life.</summary>
    public void ReleasePreviewBitmaps()
    {
        _recentThumbGeneration++;   // a fill still in flight has nothing left to settle onto
        foreach (var row in RecentMods) row.DisposeThumb();
        ClearPreviewState();
    }
}

/// <summary>One row on the home screen's recent list. It exists for the thumbnail alone:
/// <see cref="Remold.Core.RecentMod"/> is the persisted settings record and carries no UI state.</summary>
public sealed partial class RecentModVm : ObservableObject
{
    public RecentModVm(Remold.Core.RecentMod source) => Source = source;

    /// <summary>The settings record this row stands for.</summary>
    public Remold.Core.RecentMod Source { get; }
    public string Name => Source.Name;
    public string Path => Source.Path;

    /// <summary>The mod's preview at row size, or null when it has none or it couldn't be read. Owned by
    /// this row: the list disposes it on refresh and at close.</summary>
    [ObservableProperty] private Bitmap? _thumb;

    public void SetThumb(Bitmap bmp)
    {
        // dispose before overwriting, or a second fill landing on the same row leaks the one it replaces
        if (!ReferenceEquals(Thumb, bmp)) Thumb?.Dispose();
        Thumb = bmp;
    }

    public void DisposeThumb()
    {
        Thumb?.Dispose();
        Thumb = null;
    }
}
