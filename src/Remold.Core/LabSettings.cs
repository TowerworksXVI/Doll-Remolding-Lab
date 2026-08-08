using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Remold.Core;

/// <summary>One entry in the recent-mods list: the project folder + its display name. Ordered
/// most-recent-first by <see cref="LabSettings.AddRecent"/>.</summary>
public sealed class RecentMod
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Small persisted preferences: author handle, mods-library root, recent-mods list. Durable state, so
/// the JSON lives beside the exe (<see cref="LabPaths.SettingsFile"/>). Load/Save take an optional path
/// for testing. <see cref="Load"/> degrades to defaults on any error rather than throwing into the UI;
/// <see cref="Save"/> throws, and <see cref="TrySave"/> is the form that reports a failed write instead.
/// </summary>
public sealed class LabSettings
{
    public const int MaxRecent = 12;

    public string Author { get; set; } = "";
    /// <summary>The located game root (holding <c>GF2_Exilium.exe</c>); bundle/table dirs derive from it.
    /// Set once detection or a manual pick succeeds, so later launches skip the scan. Null → not located
    /// yet.</summary>
    public string? GamePath { get; set; }
    /// <summary>Where New Mod creates project folders; null → <see cref="ResolvedLibraryRoot"/>'s default.</summary>
    public string? LibraryRoot { get; set; }
    /// <summary>The 3DMigoto loader executable — its name varies by distribution (the Nexus build ships
    /// <c>3DMigoto Loader.exe</c>, SSMT fetches <c>Run.exe</c>), so the path names the exe and the
    /// <c>Mods\</c> folder a build installs into derives from its directory. USER-SET ONLY: there is no
    /// auto-detection and no default, because a wrong guess writes a mod folder into someone else's
    /// install. Null → Install and Launch stay off and say why.</summary>
    public string? MigotoLoaderExe { get; set; }
    /// <summary>Manual override for "Open in editor" (null → auto-detect, then the OS default PNG
    /// handler).</summary>
    public string? PreferredImageEditor { get; set; }
    /// <summary>Manual override for the Blender executable (null → auto-detect), so a user with several
    /// Blenders can point at the exact one.</summary>
    public string? PreferredBlender { get; set; }
    /// <summary>Optional cap on CPU cores for the app's wide parallel work: the package-time encoder (BCn
    /// encode + the per-texture loop), the build's operator solve, and the roster fill scan. Null = all
    /// cores; a settings.json without the key deserializes to null. The key name is what released installs
    /// persist, so it stays as it is.</summary>
    public int? EncoderCpuLimit { get; set; }
    /// <summary>A force rescan is still owed: the row was armed and saved, and the sweep it asked for has
    /// not run yet. Durable so the debt survives an exit — the request is recorded at the Save and the sweep
    /// waits for a rescan with nothing holding the roster, and closing the app in between must not quietly
    /// drop it. Cleared the moment the sweep is handed off to run.
    /// <para>ADDITIVE: a settings.json written by any released build has no such key and deserializes to
    /// false, which is "nothing owed" — the only correct reading of a file written before the row
    /// existed.</para></summary>
    public bool ForceRescanOwed { get; set; }
    /// <summary>Recent mod projects, most-recent-first.</summary>
    public List<RecentMod> RecentMods { get; set; } = new();

    public static string DefaultPath => LabPaths.SettingsFile;

    /// <summary>True when <see cref="Load"/> fell back to defaults because the file EXISTED but couldn't
    /// be read, as opposed to a clean first run. The caller surfaces a notice so a lost config isn't
    /// silent. Transient (not serialized); saving overwrites the bad file.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LoadedFromDefaultsAfterError { get; private set; }

    /// <summary>The effective mods-library folder: the configured <see cref="LibraryRoot"/>, else the
    /// exe-adjacent default (<see cref="LabPaths.DefaultLibraryRoot"/>).</summary>
    public string ResolvedLibraryRoot => string.IsNullOrWhiteSpace(LibraryRoot)
        ? LabPaths.DefaultLibraryRoot
        : LibraryRoot!;

    /// <summary>Where built packages land: a <c>published</c> folder SIBLING of the mods library, so a
    /// repointed library carries its outputs and finished packages never mix into project workspaces. A
    /// library at a drive root (no parent) nests <c>published</c> inside it.</summary>
    public string ResolvedPublishedRoot
    {
        get
        {
            string lib = Path.GetFullPath(ResolvedLibraryRoot);
            return Path.Combine(Path.GetDirectoryName(lib) ?? lib, "published");
        }
    }

    /// <summary>Record (or bump) a project in the recent list: de-duplicated by path, moved to the
    /// front, capped at <see cref="MaxRecent"/>.</summary>
    public void AddRecent(string path, string name)
    {
        RecentMods.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentMods.Insert(0, new RecentMod { Path = path, Name = name });
        if (RecentMods.Count > MaxRecent) RecentMods.RemoveRange(MaxRecent, RecentMods.Count - MaxRecent);
    }

    public static LabSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        bool exists = File.Exists(path);
        try
        {
            if (!exists) return new LabSettings();   // clean first run — not an error
            var s = JsonSerializer.Deserialize<LabSettings>(File.ReadAllText(path)) ?? new LabSettings();
            // an explicit null in the JSON overrides the field initializer, then consumers NRE —
            // coalesce the non-nullable members back to their defaults
            s.RecentMods ??= new();
            s.Author ??= "";
            return s;
        }
        // the file EXISTED but couldn't be read — boot on defaults and flag it so the app can say so
        catch { return new LabSettings { LoadedFromDefaultsAfterError = true }; }
    }

    /// <summary>Persist atomically (temp + move) so an interrupted write can never truncate the durable
    /// settings file: the swap replaces it whole or leaves the prior file untouched. Throws on a failed
    /// write.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic publish through a UNIQUE temp: a fixed name is one shared file, so two saves racing over
        // it publish a half-written settings file.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>Persist, reporting a failed write as false instead of throwing. False means the change
    /// lives in memory only and is lost on exit, which is a notice the caller owes the user.</summary>
    public bool TrySave(string? path = null)
    {
        try { Save(path); return true; }
        catch { return false; }
    }
}
