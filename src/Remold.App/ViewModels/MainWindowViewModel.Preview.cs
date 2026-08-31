using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Remold.App.ViewModels;

public partial class MainWindowViewModel
{
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
            string? rel;
            string root;
            int schema = Remold.Core.Project.AuthoredProjectSerializer.SchemaOf(projectPath);
            if (schema == Remold.Core.Project.AuthoredProject.CurrentSchema)
            {
                var project = Remold.Core.Project.AuthoredProjectSerializer.Load(projectPath);
                rel = project.Info.Preview;
                root = project.RootDir!;
            }
            else if (schema == Remold.Core.Project.ModProject.CurrentSchema)
            {
                // The legacy reader is adaptation input only. It must never be allowed to reinterpret a
                // schema-2 session as a mutable compatibility project, even for this manifest-only read.
                var project = Remold.Core.Project.ModProject.Load(projectPath);
                rel = project.Info.Preview;
                root = project.RootDir!;
            }
            else return null;
            if (string.IsNullOrWhiteSpace(rel)) return null;
            var path = Path.GetFullPath(Path.Combine(root, rel));
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
        BuildPage.ReleasePreview();
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
