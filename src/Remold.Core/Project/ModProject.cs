using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Remold.Core.Export;

namespace Remold.Core.Project;

/// <summary>
/// A released (schema-1) <c>mod.drlproj</c> manifest, READ. It is the input the conversion at open reads
/// and nothing else: no route builds from it, nothing writes it, and the app never holds one. A project
/// that opens is an <see cref="AuthoredProject"/> — either it already was one, or
/// <see cref="LegacyProjectAdapter"/> converted this into one, and a conversion that cannot complete
/// refuses the open rather than leaving the released shape standing.
///
/// Paths inside the file are <b>workspace-relative</b>; <see cref="RootDir"/> is set on load and is not
/// serialized — use <see cref="Resolve"/> for an absolute path.
/// </summary>
public sealed class ModProject
{
    public const int CurrentSchema = 1;
    public const string FileName = "mod.drlproj";

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    [JsonPropertyName("info")] public ProjectInfo Info { get; set; } = new();
    [JsonPropertyName("authored_against")] public AuthoredAgainst? AuthoredAgainst { get; set; }
    [JsonPropertyName("selection")] public List<SelectionEntry> Selection { get; set; } = new();
    [JsonPropertyName("targets")] public List<ProjectTarget> Targets { get; set; } = new();
    /// <summary>The meshes the released workbench's Hide toggle had switched off, by subject-scoped
    /// identity. The conversion reads each as a hide edit on that part. Mutate through
    /// <see cref="SetHidden"/> so the one-entry-per-mesh invariant holds.</summary>
    [JsonPropertyName("hidden")] public List<HiddenMesh> Hidden { get; set; } = new();
    /// <summary>The changes the released Build pane had unticked — a part's subject-scoped identity plus
    /// the verb of the change it was ticked against. The conversion keeps such an edit and gives it no
    /// activation placement. Mutate through <see cref="SetBuildExcluded"/> so the one-entry-per-(mesh,
    /// verb) invariant holds.</summary>
    [JsonPropertyName("build_excluded")] public List<ExcludedEdit> BuildExcluded { get; set; } = new();
    /// <summary>The per-change toggle keys the released Build pane had bound — a part's subject-scoped
    /// identity plus its verb, and the key that gates it in game. The conversion reads each into a key
    /// group. Mutate through <see cref="SetChangeKey"/> so the one-entry-per-(mesh, verb) invariant
    /// holds.</summary>
    [JsonPropertyName("change_keys")] public List<ChangeKey> ChangeKeys { get; set; } = new();
    /// <summary>The toon ramps picked on materials of parts this mod does NOT replace — the one piece of
    /// per-material state the project holds, since a ramp is the one map a material can be given without
    /// its geometry or any of its pictures changing. Null (not an empty list) where none is picked, so a
    /// project that has never picked one is written exactly as it was before the slot existed. Mutate
    /// through <see cref="SetStockRamp"/> so the one-entry-per-material invariant holds.</summary>
    [JsonPropertyName("stock_ramps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StockRampPick>? StockRamps { get; set; }

    /// <summary>Absolute path to the project folder. Set by <see cref="Load"/>/<see cref="Save"/> from the
    /// file location. Not part of the on-disk manifest.</summary>
    [JsonIgnore] public string? RootDir { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // System.Text.Json ignores unknown members by default, so an additive key (which stays schema 1)
        // loads without error on a reader that predates it.
    };

    /// <summary>Whether the ledger carries this subject (character · outfit stem) as a
    /// <see cref="SelectionEntry"/>, case-insensitive. The one authoritative "is this subject in the mod"
    /// predicate — the commit-hop guard and the Pick tree's checkboxes both go through it.</summary>
    public bool HasSubject(string character, string stem) =>
        Selection.Any(s =>
            string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Outfit, stem, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the workbench has hidden this mesh. Case-insensitive on all three identity
    /// parts, matching <see cref="HasSubject"/>.</summary>
    public bool IsHidden(string character, string stem, string mesh) =>
        Hidden.Any(h => h.IsForSubject(character, stem)
            && string.Equals(h.Mesh, mesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>Set one mesh's Hide toggle — the single write route that keeps <see cref="Hidden"/> at
    /// one entry per mesh regardless of which flow mutates it.</summary>
    public void SetHidden(string character, string stem, string mesh, bool hidden)
    {
        var existing = Hidden.Find(h => h.IsForSubject(character, stem)
            && string.Equals(h.Mesh, mesh, StringComparison.OrdinalIgnoreCase));
        if (hidden && existing is null)
            Hidden.Add(new HiddenMesh { Character = character, Outfit = stem, Mesh = mesh });
        else if (!hidden && existing is not null)
            Hidden.Remove(existing);
    }

    /// <summary>Drop every Hide of a removed subject, so unchecking a subject can't leave its hides behind
    /// to resurface on a re-add. Returns the number dropped.</summary>
    public int RemoveSubjectHidden(string character, string stem) =>
        Hidden.RemoveAll(h => h.IsForSubject(character, stem));

    /// <summary>Whether the Build pane has unticked this derived edit. The verb is part of the identity (see
    /// <see cref="ExcludedEdit"/>), so a tick never carries onto a change derived under another
    /// verb.</summary>
    public bool IsBuildExcluded(string character, string stem, string mesh, string verb) =>
        BuildExcluded.Any(x => x.IsForSubject(character, stem)
            && string.Equals(x.Mesh, mesh, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Verb, verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>Set one derived edit's build-exclusion — the single write route that keeps
    /// <see cref="BuildExcluded"/> at one entry per (mesh, verb) regardless of which flow mutates it.</summary>
    public void SetBuildExcluded(string character, string stem, string mesh, string verb, bool excluded)
    {
        var existing = BuildExcluded.Find(x => x.IsForSubject(character, stem)
            && string.Equals(x.Mesh, mesh, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Verb, verb, StringComparison.OrdinalIgnoreCase));
        if (excluded && existing is null)
            BuildExcluded.Add(new ExcludedEdit { Character = character, Outfit = stem, Mesh = mesh, Verb = verb });
        else if (!excluded && existing is not null)
            BuildExcluded.Remove(existing);
    }

    /// <summary>Drop every build-exclusion of a removed subject, so unchecking a subject can't leave them
    /// behind to resurface on a re-add. Returns the number dropped.</summary>
    public int RemoveSubjectBuildExcluded(string character, string stem) =>
        BuildExcluded.RemoveAll(x => x.IsForSubject(character, stem));

    /// <summary>This derived change's whole key binding, or null when it carries none. The verb is part of
    /// the identity for the same reason it is on <see cref="ExcludedEdit"/>.</summary>
    public ChangeKey? FindChangeKey(string character, string stem, string mesh, string verb) =>
        ChangeKeys.Find(k => k.IsForSubject(character, stem)
            && string.Equals(k.Mesh, mesh, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.Verb, verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>This derived change's toggle key, or null when it has none (the change is always on).</summary>
    public string? GetChangeKey(string character, string stem, string mesh, string verb) =>
        FindChangeKey(character, stem, mesh, verb)?.Key;

    /// <summary>Bind (or clear, with a null/blank key) one derived change's toggle key — the single write
    /// route that keeps <see cref="ChangeKeys"/> at one entry per (mesh, verb). An unusable key string
    /// clears the binding rather than persisting something the emitter would have to refuse later.
    ///
    /// <para>The call states the WHOLE binding: how the change starts and what its off state means both
    /// travel with the key, so a rebind that names neither sets neither, and a clear takes them with the
    /// entry.</para></summary>
    public void SetChangeKey(string character, string stem, string mesh, string verb, string? key,
        bool hideWhenOff = false, bool startsOff = false)
    {
        var normalized = ModKeys.Normalize(key);
        var existing = FindChangeKey(character, stem, mesh, verb);
        if (normalized is null)
        {
            if (existing is not null) ChangeKeys.Remove(existing);
            return;
        }
        if (existing is not null)
        {
            existing.Key = normalized;
            existing.HideWhenOff = hideWhenOff;
            existing.StartsOff = startsOff;
        }
        else ChangeKeys.Add(new ChangeKey
        {
            Character = character, Outfit = stem, Mesh = mesh, Verb = verb, Key = normalized,
            HideWhenOff = hideWhenOff, StartsOff = startsOff,
        });
    }

    /// <summary>Drop every toggle key of a removed subject, so unchecking a subject can't leave a binding
    /// behind to resurface on a re-add. Returns the number dropped.</summary>
    public int RemoveSubjectChangeKeys(string character, string stem) =>
        ChangeKeys.RemoveAll(k => k.IsForSubject(character, stem));

    /// <summary>The ramp picked on one material of one unreplaced part, or null when it carries none.
    /// Case-insensitive on every identity part, as everywhere else.</summary>
    public StockRampPick? FindStockRamp(string character, string stem, string mesh, string material) =>
        StockRamps?.Find(r => r.IsForSubject(character, stem)
            && string.Equals(r.Mesh, mesh, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Material, material, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pick (or clear, with a null path) one material's toon ramp — the single write route that
    /// keeps <see cref="StockRamps"/> at one entry per (mesh, material). The slot has the same two states
    /// a replaced submesh's has: a file, or nothing at all, in which case the game's own ramp draws and the
    /// build ships nothing for it.</summary>
    public void SetStockRamp(string character, string stem, string mesh, string material,
        string? projectRelativeDds)
    {
        var existing = FindStockRamp(character, stem, mesh, material);
        if (projectRelativeDds is null)
        {
            if (existing is not null) StockRamps!.Remove(existing);
            if (StockRamps is { Count: 0 }) StockRamps = null;   // back to the shape a project with none has
            return;
        }
        if (existing is not null) { existing.Ramp = projectRelativeDds; return; }
        (StockRamps ??= new List<StockRampPick>()).Add(new StockRampPick
        {
            Character = character, Outfit = stem, Mesh = mesh, Material = material,
            Ramp = projectRelativeDds,
        });
    }

    /// <summary>Drop every ramp pick of a removed subject, so unchecking a subject can't leave one behind
    /// to resurface on a re-add. Returns the number dropped.</summary>
    public int RemoveSubjectStockRamps(string character, string stem)
    {
        int dropped = StockRamps?.RemoveAll(r => r.IsForSubject(character, stem)) ?? 0;
        if (StockRamps is { Count: 0 }) StockRamps = null;
        return dropped;
    }

    /// <summary>Resolve a workspace-relative path against <see cref="RootDir"/>.</summary>
    public string Resolve(string relative)
    {
        if (RootDir is null) throw new InvalidOperationException("project has no RootDir (load or save it first)");
        return Path.GetFullPath(Path.Combine(RootDir, relative));
    }

    /// <summary>The path to the manifest file for a given project folder.</summary>
    public static string ManifestPathFor(string folder) => Path.Combine(folder, FileName);

    /// <summary>A folder under <paramref name="root"/> named <paramref name="slug"/>, suffixed <c>-2</c>,
    /// <c>-3</c>… when that name is taken, so two projects with the same slug don't collide on
    /// disk.</summary>
    public static string UniqueDir(string root, string slug)
    {
        var dir = Path.Combine(root, slug);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(root, $"{slug}-{i}");
        return dir;
    }

    /// <summary>True when <paramref name="folderName"/> is already the correct folder for
    /// <paramref name="slug"/> — the bare slug or one of its <see cref="UniqueDir"/> dedup forms. A folder
    /// named <c>untitled-mod-5</c> IS the right home for the slug <c>untitled-mod</c> and must NOT read as a
    /// rename mismatch: a check that ignores the dedup suffix re-moves the folder on every autosave,
    /// stranding files across the freed/re-taken suffixes.</summary>
    public static bool FolderMatchesSlug(string folderName, string slug)
    {
        if (string.Equals(folderName, slug, StringComparison.OrdinalIgnoreCase)) return true;
        if (folderName.Length <= slug.Length + 1) return false;
        if (!folderName.StartsWith(slug, StringComparison.OrdinalIgnoreCase) || folderName[slug.Length] != '-')
            return false;
        var suffix = folderName.AsSpan(slug.Length + 1);
        // a genuine dedup suffix is an integer ≥ 2 (UniqueDir starts at -2); anything else is a different name
        return int.TryParse(suffix, out int n) && n >= 2;
    }

    /// <summary>Load a project. <paramref name="path"/> may be the project folder or the <c>mod.drlproj</c>
    /// file itself. Throws <see cref="FileNotFoundException"/> if the manifest is missing and
    /// <see cref="InvalidDataException"/> if it can't be parsed — a corrupt project is a clear error, never a
    /// silent reset.</summary>
    public static ModProject Load(string path)
    {
        var file = Directory.Exists(path) ? ManifestPathFor(path) : path;
        if (!File.Exists(file))
            throw new FileNotFoundException($"mod project not found: {file}", file);

        string json = File.ReadAllText(file);
        int schema = AuthoredProjectSerializer.ReadSchema(json);
        if (schema != CurrentSchema)
            throw new InvalidDataException($"ModProject.Load accepts schema {CurrentSchema} only; "
                + "use AuthoredProjectDocument for authored projects");

        ModProject? proj;
        try { proj = JsonSerializer.Deserialize<ModProject>(json, Json); }
        catch (JsonException e) { throw new InvalidDataException($"mod project is not valid JSON: {file}", e); }
        if (proj is null) throw new InvalidDataException($"mod project is empty: {file}");

        proj.RootDir = Path.GetDirectoryName(Path.GetFullPath(file));
        return proj;
    }

    /// <summary>Write the manifest. With no argument it writes <c>&lt;RootDir&gt;/mod.drlproj</c>; a folder
    /// or file <paramref name="path"/> overrides that and (re)sets <see cref="RootDir"/>.</summary>
    public void Save(string? path = null)
    {
        string file;
        if (path is null)
        {
            if (RootDir is null) throw new InvalidOperationException("Save() needs a path or a RootDir");
            file = ManifestPathFor(RootDir);
        }
        else
        {
            file = Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar) || !path.EndsWith(FileName, StringComparison.OrdinalIgnoreCase)
                ? ManifestPathFor(path)
                : path;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(dir);
        RootDir = dir;
        AtomicWrite(file, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Persist, minting the project folder under <paramref name="libraryRoot"/> named
    /// <paramref name="slug"/> when <see cref="RootDir"/> is still null. The single first-save mint route, so
    /// "no folder until a save actually happens" lives in one place. Returns the folder written; throws on a
    /// write failure for the caller to surface.</summary>
    public string SaveOrMint(string libraryRoot, string slug)
    {
        if (RootDir is null)
            RootDir = UniqueDir(libraryRoot, slug);
        Save();
        return RootDir!;
    }

    /// <summary>The last-good manifest sidecar (<c>mod.drlproj.bak</c>): the previous manifest, left beside
    /// the live one on every save after the first, so a crash can never leave the project with no complete
    /// ledger.</summary>
    public static string BackupPathFor(string folder) => ManifestPathFor(folder) + ".bak";

    /// <summary>Write the manifest so a crash/full-disk/AV interruption can never truncate the sole project
    /// ledger. The JSON goes to a same-directory temp first (keeping the swap on one volume), then atomically
    /// replaces the live file, moving the outgoing manifest aside as <c>mod.drlproj.bak</c>. A failure throws
    /// rather than leave a half-written live file.</summary>
    private static void AtomicWrite(string file, string json)
    {
        var enc = new UTF8Encoding(false);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json, enc);
        if (File.Exists(file))
            File.Replace(tmp, file, file + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, file);   // first save — no live manifest to replace
    }

    /// <summary>Move the whole project folder to <paramref name="destFolder"/> and retarget
    /// <see cref="RootDir"/>. Workspace-relative paths are unchanged (they're relative to the folder), so
    /// targets/originals keep resolving. <paramref name="destFolder"/> must not already exist.</summary>
    public void MoveTo(string destFolder)
    {
        if (RootDir is null) throw new InvalidOperationException("project has no RootDir to move");
        Directory.Move(RootDir, destFolder);
        RootDir = Path.GetFullPath(destFolder);
    }

    /// <summary>Copy the project's INPUTS (manifest, workspace assets, <c>originals/</c>) to
    /// <paramref name="destFolder"/> (which must not exist) and return the project loaded from the copy. The
    /// manifest's temp/last-good sidecars are NOT carried: they belong to the source's save
    /// history.</summary>
    public ModProject CopyTo(string destFolder)
    {
        if (RootDir is null) throw new InvalidOperationException("project has no RootDir to copy");
        CopyInputs(RootDir, destFolder);
        return Load(destFolder);
    }

    /// <summary>True when <paramref name="fileName"/> is a generated save artifact — the manifest's
    /// <c>.tmp</c>/<c>.bak</c> sidecars. Excluded from <see cref="CopyTo"/>.</summary>
    private static bool IsGeneratedArtifact(string fileName) =>
        fileName.Equals(FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(FileName + ".bak", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("~asset.", StringComparison.OrdinalIgnoreCase);

    private static bool IsExternalSessionArtifact(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals(".editor", StringComparison.OrdinalIgnoreCase)
            || first.Equals(ProjectAssetIngress.DirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyInputs(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            if (IsExternalSessionArtifact(src, dir)) continue;
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));
        }
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            if (IsExternalSessionArtifact(src, file)) continue;
            if (IsGeneratedArtifact(Path.GetFileName(file))) continue;   // save sidecars stay with the source
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(src, file)), overwrite: false);
        }
    }

}

/// <summary>The mod's display/identity metadata.</summary>
public sealed class ProjectInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("outfit")] public string? Outfit { get; set; }
    /// <summary>Workspace-relative path to the preview image, if any.</summary>
    [JsonPropertyName("preview")] public string? Preview { get; set; }
    /// <summary>Tier-1 toggle key: the one key that turns the WHOLE mod on and off in game (see
    /// <see cref="ModKeys"/>). Null = no key, and the mod is always on — the emission this build has
    /// always produced.</summary>
    [JsonPropertyName("toggle_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToggleKey { get; set; }

    /// <summary>Whether the build ships the record that lets the mod folder be read back into a project
    /// (<see cref="Migoto.RepairData"/>). True is the default and what a manifest that names it not takes,
    /// so a project written before the option existed keeps shipping the record. False ships the mod
    /// without it, which is the author's call to make: nothing else about the mod changes, and the folder
    /// is then exactly as readable as one built before the record existed.</summary>
    [JsonPropertyName("include_repair_data")] public bool IncludeRepairData { get; set; } = true;
}

/// <summary>The game build the exports were read from — for post-update staleness.</summary>
public sealed class AuthoredAgainst
{
    /// <summary>The asset-catalog version (kept as a string to match <c>GameVfs.CatalogVersion</c>).</summary>
    [JsonPropertyName("catalog_version")] public string? CatalogVersion { get; set; }
}

/// <summary>The stamp/compare rules for <see cref="AuthoredAgainst"/>. A mod is "authored against" the
/// catalog version it last materialized/built under; a mismatch on open is the post-update "needs re-check"
/// signal.</summary>
public static class AuthoredAgainstPolicy
{
    /// <summary>The catalog version to persist when the project touches game data: always the live version.
    /// A null live version (game not loaded) leaves the existing stamp untouched.</summary>
    public static string? StampFor(string? existingStamp, string? liveCatalog) => liveCatalog ?? existingStamp;

    /// <summary>Whether an open project should warn it was authored against an OLDER game version: a stamp is
    /// present, the game is loaded, and the two differ.</summary>
    public static bool NeedsStaleNotice(string? stamp, string? liveCatalog) =>
        stamp is not null && liveCatalog is not null && !string.Equals(stamp, liveCatalog, StringComparison.Ordinal);
}

/// <summary>One Pick selection, persisted so the tree restores on open. Selection is <b>subject-level</b> —
/// a row keys only on (character, outfit); the mod's touched set is the target list.</summary>
public sealed class SelectionEntry
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    /// <summary>The outfit stem (e.g. <c>KarstSR01</c>).</summary>
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
}

/// <summary>One workbench-hidden mesh, by the same subject-scoped triple every other released row uses.
/// <see cref="Mesh"/> is the roster mesh <c>m_Name</c> (the representative <c>_lod0</c> slot name); tier
/// fan-out happens at build from the live recipe.</summary>
public sealed class HiddenMesh
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";

    public bool IsForSubject(string character, string stem) =>
        string.Equals(Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Outfit, stem, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One derived edit the Build pane has unticked, by the same subject-scoped identity as a
/// <see cref="HiddenMesh"/>.
///
/// <para><b><see cref="Verb"/> is part of the identity.</b> One mesh's derived verb changes with the
/// workbench state (Hide outranks Replace, Replace outranks Retexture), so an exclusion keyed on the mesh
/// alone would carry a tick recorded against one verb onto a different change the modder never unticked,
/// silently unshipping it.</para></summary>
public sealed class ExcludedEdit
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";
    /// <summary>One of <see cref="EditVerbs"/> — the verb the unticked row was derived as.</summary>
    [JsonPropertyName("verb")] public string Verb { get; set; } = "";

    public bool IsForSubject(string character, string stem) =>
        string.Equals(Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Outfit, stem, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One derived change's tier-2 toggle key, by the same subject-scoped identity (verb included) as
/// an <see cref="ExcludedEdit"/>. <see cref="Key"/> is always a <see cref="ModKeys.Normalize"/>d string —
/// <see cref="ModProject.SetChangeKey"/> is the only writer.</summary>
public sealed class ChangeKey
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";
    /// <summary>One of <see cref="EditVerbs"/> — the verb the keyed row was derived as.</summary>
    [JsonPropertyName("verb")] public string Verb { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>Replace only: with the key off, the part is absent rather than back to the character's own.
    /// False (the default, and what an entry that names it not carries) reverts to vanilla, which is how a
    /// replacement is compared against stock. A Hide has no donor, so vanilla is its only off.</summary>
    [JsonPropertyName("hide_when_off")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideWhenOff { get; set; }

    /// <summary>The change ships off and the first press turns it on. False (the default) ships it on, so a
    /// keyed change behaves as an unkeyed one until the key is pressed. Per session: a press holds for the
    /// run it was made in, and the next launch starts here again.</summary>
    [JsonPropertyName("starts_off")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StartsOff { get; set; }

    public bool IsForSubject(string character, string stem) =>
        string.Equals(Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Outfit, stem, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One toon ramp picked on a material of a part this mod does NOT mesh-replace, by the same subject-scoped
/// identity as a <see cref="HiddenMesh"/> plus the material it belongs to.
///
/// <para><b>Identity is the MATERIAL, never a texture hash.</b> The runtime's texture hash reads only the
/// head of a texture, which is far less than a ramp: two ramps drawing different curves collide on it. The
/// subject scopes the pick (same-named assets in different bundles belong to different subjects here), the
/// renderer slot name selects the part, and the material's own name selects within it.</para>
///
/// <para>The build turns this into a draw-scoped bind at the part's own draws, gated on one of the
/// material's ordinary maps — which DO hash soundly — being sighted there.</para>
/// </summary>
public sealed class StockRampPick
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    /// <summary>The part's renderer slot name, as every other subject-scoped record spells it.</summary>
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";
    /// <summary>The material's <c>m_Name</c>.</summary>
    [JsonPropertyName("material")] public string Material { get; set; } = "";
    /// <summary>Workspace-relative path to the picked ramp — a <c>.dds</c> in the game's own float format,
    /// shipped byte for byte. The slot's other state is having no entry here at all.</summary>
    [JsonPropertyName("ramp")] public string Ramp { get; set; } = "";

    public bool IsForSubject(string character, string stem) =>
        string.Equals(Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Outfit, stem, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One tracked replacement, plus the editing state the project needs to round-trip. All file paths
/// are workspace-relative.</summary>
public sealed class ProjectTarget
{
    [JsonPropertyName("asset_type")] public string AssetType { get; set; } = "";   // "Mesh" | "Texture2D"
    [JsonPropertyName("bundle")] public string Bundle { get; set; } = "";          // 32-hex source bundle
    /// <summary>On a Mesh target this is the RENDERER SLOT's GameObject name, which the workbench tree, the
    /// Pick roster, the verb derivation and the build all key a part by, and which the part's workspace glb
    /// carries it under. It is not necessarily the mesh asset's <c>m_Name</c>: on smr/static slots the two
    /// diverge, and <see cref="PathId"/> is what selects the object there. On a Texture2D target it IS the
    /// asset's own name — that route reads its texture by name.</summary>
    [JsonPropertyName("object_name")] public string ObjectName { get; set; } = "";
    /// <summary>Exact game-version-specific selector, the alternative to <see cref="ObjectName"/>. Set on
    /// smr-body mesh targets, where enemy bundles ship same-named mesh copies so the name alone can select
    /// the wrong one; null on name-selected targets.</summary>
    [JsonPropertyName("path_id")] public long? PathId { get; set; }
    /// <summary>Texture only — the recorded object name of EVERY mesh that uses this texture. A texture is stored once per
    /// (<see cref="Bundle"/>, <see cref="ObjectName"/>) identity, since the game carries same-named textures
    /// in different bundles as distinct assets, and shown under each of these meshes in the Edit pane;
    /// editing the one shared file propagates to them all.</summary>
    [JsonPropertyName("users")] public List<string>? Users { get; set; }
    /// <summary>Mesh only — the (character, outfit stem) subject this target was materialized for, so a
    /// subject-scoped remove owns its meshes by PERSISTED IDENTITY, not by the workspace subfolder its
    /// <see cref="ReplaceFile"/> happens to sit under. Null on textures and on a mesh whose subject couldn't
    /// be attributed — such a target belongs to no subject, and is never guessed.</summary>
    [JsonPropertyName("subject_character")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectCharacter { get; set; }
    [JsonPropertyName("subject_outfit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectOutfit { get; set; }
    /// <summary>Workspace-relative path to the .glb/.png that replaces the asset.</summary>
    [JsonPropertyName("replace_file")] public string ReplaceFile { get; set; } = "";
    /// <summary>Workspace-relative path to the pristine, unedited baseline for delta/restore and the
    /// <see cref="ModProject.IsEdited"/> byte-compare. The Add-time export until the lazy open-in-Blender rig
    /// build re-copies the rigged (still unedited) glb over it: the baseline tracks the workspace's unedited
    /// state, not a frozen Add snapshot.</summary>
    [JsonPropertyName("original_file")] public string? OriginalFile { get; set; }
    [JsonPropertyName("original_verts")] public int? OriginalVerts { get; set; }
    /// <summary>Mesh only — the authored texture set per DONOR submesh, used when this target is edited (its
    /// build verb is a Replace, so the workspace glb IS the donor). A donor submesh with no entry inherits
    /// the anchor part's maps at the draw.</summary>
    [JsonPropertyName("donor_textures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubmeshTextures>? DonorTextures { get; set; }
    /// <summary>Mesh only — the material name of EVERY donor submesh, in submesh order, recorded at the
    /// send-back: what the modder sees in Blender's slot list. The Edit pane presents an edited part by these
    /// rather than by the game renderer's slots, falling back to the renderer's when null.</summary>
    [JsonPropertyName("donor_materials")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DonorMaterials { get; set; }
    [JsonPropertyName("edited")] public bool Edited { get; set; }
    /// <summary>Texture only — the resolver source that produced this target (<c>renderer</c> from an export,
    /// <c>material</c> from a workbench materialize). Persisted for diagnostics.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
    /// <summary>Mesh only — the part's other LOD-tier slots, each fanned out at build to the same edited
    /// high-detail glb. Resolved at export so the build needs no game-dir access.</summary>
    [JsonPropertyName("lod_slots")] public List<LodSlot>? LodSlots { get; set; }
    /// <summary>Mesh only — the scene-rest uprighting baked into the workspace glb at export (16 floats,
    /// row-major System.Numerics; see <see cref="Mesh.RestBake"/>). The build applies the INVERSE before
    /// writing the game-space payload. Null = nothing baked.</summary>
    [JsonPropertyName("baked_rest")] public List<float>? BakedRest { get; set; }
}
