using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>A schema-aware project document, and the home of the project's ONE mutable model.
///
/// <para>Authored intent is both the persisted owner and the only thing anything writes to: the document
/// holds the single <see cref="AuthoredEditSession"/> and every surface's verbs go through it. A save
/// serializes what the session holds.</para>
///
/// <para>A released schema-1 manifest is converted ONCE, when it opens, and it is the only support the
/// released shape has. What that conversion produced is the session from then on, and the first save
/// migrates the file, keeping the outgoing manifest as the one-time backup. A manifest that CANNOT be
/// converted does not open at all: no game files to convert it against, or a route the install cannot
/// answer for, and the open fails naming which. Nothing writes schema 1, and no surface has a released
/// project to draw.</para>
/// </summary>
public sealed class AuthoredProjectDocument
{
    private AuthoredProjectDocument(AuthoredEditSession session, bool openedLegacy)
    {
        Session = session;
        OpenedLegacy = openedLegacy;
    }

    /// <summary>The one mutable owner of this project's intent.</summary>
    public AuthoredEditSession Session { get; }

    /// <summary>A detached read of what the session holds. Every get is a fresh snapshot — handing out a
    /// reference to the live model would make a second owner of it.</summary>
    public AuthoredProject Authored => Session.Snapshot();

    /// <summary>This project arrived as a schema-1 manifest and was converted at open. It stays true until
    /// the first save migrates the file, which is the save that reports what the conversion inferred.</summary>
    public bool OpenedLegacy { get; private set; }

    /// <summary>What the conversion at open inferred, for the first save to report. Null on a project that
    /// was already schema 2 — a conversion that could not complete threw instead.</summary>
    public MigrationReport? LastMigrationReport { get; private set; }

    /// <summary>What an open says when a schema-1 mod cannot be converted because the game files it has to
    /// be re-anchored against are not loaded. User error, not a defect: the remedy is the install, said in
    /// the app's own words for that state (<see cref="GameFilesGate.Unavailable"/>).</summary>
    public const string NoInstall =
        "This mod uses an older project format and has to be updated before it opens. "
        + GameFilesGate.Unavailable;

    /// <summary>A fresh untitled project, authored from its first keystroke. A new mod has no released past
    /// to convert, so there is nothing to adapt and its first save writes schema 2.</summary>
    public static AuthoredProjectDocument New() =>
        new(new AuthoredEditSession(new AuthoredProject()), openedLegacy: false);

    /// <summary>Open a project. A schema-1 manifest is converted here or the open FAILS — the conversion is
    /// the only support the released shape has, so a project that cannot take it is not opened on it.</summary>
    /// <param name="rosterSlots">The install's parts per subject, paired with <paramref name="resolvePart"/>
    /// — see <see cref="LegacyProjectAdapter.Adapt"/>, which joins a released texture edit against it.</param>
    /// <exception cref="InvalidDataException">The file is a schema this app does not read, or a schema-1
    /// manifest this install cannot convert. The message is what the modder is shown.</exception>
    public static AuthoredProjectDocument Load(string path,
        Func<TargetPart, LegacyResolvedPart?>? resolvePart = null,
        Func<string, string, IReadOnlyList<string>>? rosterSlots = null)
    {
        int schema = AuthoredProjectSerializer.SchemaOf(path);
        if (schema == AuthoredProject.CurrentSchema)
            return new AuthoredProjectDocument(
                new AuthoredEditSession(AuthoredProjectSerializer.Load(path)), openedLegacy: false);
        if (schema != ModProject.CurrentSchema)
            throw new InvalidDataException(schema > AuthoredProject.CurrentSchema
                ? AuthoredProjectSerializer.NewerProject : AuthoredProjectSerializer.DamagedProject);
        if (resolvePart is null) throw new InvalidDataException(NoInstall);

        var adaptation = LegacyProjectAdapter.Adapt(ModProject.Load(path), resolvePart, rosterSlots);
        // The refusal is one sentence; the conversion's own itemized account rides underneath it, so the
        // surface that reports a failed open writes the whole thing to its log the way a successful
        // conversion's report is written.
        if (!adaptation.Report.CanSave)
            throw new InvalidDataException(CannotUpdate(adaptation.Report),
                new InvalidDataException(ReportForTheLog(adaptation.Report)));
        return new AuthoredProjectDocument(new AuthoredEditSession(adaptation.Project),
            openedLegacy: true)
        {
            LastMigrationReport = adaptation.Report,
        };
    }

    /// <summary>The conversion's complete account, one item per line, in the shape a reader of the log
    /// expects: what it was about, what it says, and how it was disposed of.</summary>
    public static string ReportForTheLog(MigrationReport report) =>
        report.Items.Count == 0 ? "No adjustments were needed."
            : string.Join(Environment.NewLine, report.Items.Select(item =>
                $"{item.Scope}: {item.Detail} [{item.Disposition} · {item.Code}]"));

    /// <summary>What an open says when the conversion itself could not finish: which parts of the mod could
    /// not be updated, up to three and a count past that, and then the cause. A refusal that named only the
    /// routes said what failed and never why, and a problem the whole project carries has no part to name at
    /// all, so it gets its own sentence rather than one reading "…: project".
    ///
    /// <para>A part is named by its RENDERER SLOT — the name the ② Edit page itself shows for a part whose
    /// subject the install has not read yet. The short token ("cloth1") is read off a warmed subject model,
    /// which an open that is refusing does not have.</para>
    ///
    /// <para>No fix line: nothing the conversion refuses on has an action in this app, and an invented one
    /// costs the modder the time to try it.</para></summary>
    internal static string CannotUpdate(MigrationReport report)
    {
        var blocking = report.Items.Where(item => item.BlocksSave).ToList();
        var parts = blocking.Select(item => PartNamed(item.Scope)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string cause = blocking.Where(item => item.DetailIsForTheScreen).Select(item => item.Detail)
            .FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail)) ?? "";
        string said = parts.Count == 0
            ? "Couldn't update it." + (cause.Length == 0 ? "" : $" {Sentence(cause)}")
            : "Couldn't update these parts of it: " + string.Join(", ", parts.Take(3))
              + (parts.Count > 3 ? $", and {parts.Count - 3} more" : "") + "."
              + (cause.Length == 0 ? "" : $" {Sentence(cause)}");
        return "This mod uses an older project format and has to be updated before it opens. " + said;
    }

    /// <summary>One blocking item's scope as a PART name, or null where it names no part: the conversion
    /// files a route as "subject / outfit / renderer slot" and a project-wide problem as
    /// <see cref="LegacyProjectAdapter.ProjectScope"/>, and only the slot is something the modder can look
    /// for.</summary>
    private static string? PartNamed(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)
            || string.Equals(scope.Trim(), LegacyProjectAdapter.ProjectScope,
                StringComparison.OrdinalIgnoreCase)) return null;
        string slot = scope.Split('/')[^1].Trim();
        return slot.Length == 0 ? null : slot;
    }

    private static string Trimmed(string detail) => detail.Trim().TrimEnd('.');

    private static string Sentence(string detail) =>
        char.ToUpperInvariant(Trimmed(detail)[0]) + Trimmed(detail)[1..] + ".";

    /// <summary>Persist what the session holds, as schema 2. A released schema-1 file on disk is replaced
    /// atomically and retained as <c>mod.drlproj.bak</c>; a schema-2 file is simply rewritten.</summary>
    public void Save(string? path = null) => Write(Session, SaveTarget(path));

    private void Write(AuthoredEditSession session, string target)
    {
        string file = Directory.Exists(target) || !target.EndsWith(ModProject.FileName,
                StringComparison.OrdinalIgnoreCase)
            ? ModProject.ManifestPathFor(target) : target;
        string? valueRoot = session.Snapshot().RootDir;
        var removedValueFiles = session.SweepStructuredValuesForSave();
        var authored = session.Snapshot();
        bool legacyOnDisk = File.Exists(file)
            && AuthoredProjectSerializer.SchemaOf(file) == ModProject.CurrentSchema;
        if (legacyOnDisk) AuthoredProjectSerializer.SaveMigrated(authored, file);
        else AuthoredProjectSerializer.Save(authored, file);
        SweepStructuredValueFiles(valueRoot, authored, removedValueFiles);
        // Both writers stamp the folder they wrote into onto the project they were handed; that instance is a
        // snapshot, so the answer is carried back to the live model rather than lost with it.
        if (authored.RootDir is { } root) session.SetRootDir(root);
        OpenedLegacy = false;
    }

    private static void SweepStructuredValueFiles(string? root, AuthoredProject project,
        IReadOnlyList<string> removedFiles)
    {
        if (root is null) return;
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var kept = project.ProjectAssets.Select(asset => Normalize(asset.File))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = removedFiles.Select(file => Path.Combine(root,
                file.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();
        string values = Path.Combine(root, "values");
        if (Directory.Exists(values))
            candidates.AddRange(Directory.EnumerateFiles(values, "*.stage",
                SearchOption.AllDirectories));
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(candidate);
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Normalize(Path.GetRelativePath(root, full));
            if (kept.Contains(relative) || !File.Exists(full)) continue;
            File.Delete(full);
        }

        static string Normalize(string file) => file.Replace('\\', '/');
    }

    private string SaveTarget(string? path) =>
        path ?? Session.Snapshot().RootDir
        ?? throw new InvalidOperationException("project save needs a path or a root folder");

    public void RebaseRoot(string root) => Session.SetRootDir(root);

    public void MoveTo(string destination)
    {
        string source = Session.Snapshot().RootDir
            ?? throw new InvalidOperationException("project has no root directory to move");
        Directory.Move(source, destination);
        RebaseRoot(Path.GetFullPath(destination));
    }

    /// <summary>Copy the persisted project inputs to a new folder. The source is saved first, so what is
    /// copied is schema 2; transport scratch and save sidecars are not copied.</summary>
    public AuthoredProjectDocument CopyTo(string destination)
    {
        string source = Session.Snapshot().RootDir
            ?? throw new InvalidOperationException("project has no root directory to copy");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"destination already exists: {destination}");
        CopyInputs(source, destination);
        return Load(destination);
    }

    private static void CopyInputs(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            if (ExternalArtifact(relative)) continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (ExternalArtifact(relative)) continue;
            string name = Path.GetFileName(file);
            if (name.Equals(ModProject.FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
                || name.Equals(ModProject.FileName + ".bak", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("~asset.", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static bool ExternalArtifact(string relative)
    {
        string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals(".editor", StringComparison.OrdinalIgnoreCase)
            || first.Equals(ProjectAssetIngress.DirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-anchor parts against what THIS project already recorded, rather than against a mounted
    /// install. Every route in a schema-2 project names an exact game object — the validator refuses one that
    /// does not, and every way in and out of the model goes through it — so the recorded references answer
    /// for every part the project already touches. A part it has never touched answers null, which is the
    /// honest "only the install can name this".</summary>
    public Func<TargetPart, LegacyResolvedPart?> RecordedResolver() =>
        RecordedResolver(Session.Snapshot());

    private static Func<TargetPart, LegacyResolvedPart?> RecordedResolver(AuthoredProject project)
    {
        var assets = project.ProjectAssets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var assetSourcesBySlot = project.EditDefinitions.SelectMany(edit => edit.Bindings)
            .Where(binding => binding.ProjectAssetId is not null
                && assets.GetValueOrDefault(binding.ProjectAssetId)?.Source?.GameAsset is not null)
            .GroupBy(binding => binding.SlotId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(binding => assets[binding.ProjectAssetId!].Source!.GameAsset!)
                    .DistinctBy(ObjectKey).ToList(), StringComparer.Ordinal);

        return target =>
        {
            var slots = project.TargetSlots.Where(slot => slot.Part.SameAs(target)).ToList();
            var baseSlot = slots.FirstOrDefault(slot => slot.Input == TargetInputKind.Geometry
                    && (slot.Tier is null || string.Equals(slot.Tier, "lod0",
                        StringComparison.OrdinalIgnoreCase)))
                ?? slots.FirstOrDefault(slot => slot.Tier is null
                    || string.Equals(slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase));
            if (baseSlot?.Mesh is null) return null;

            var materials = slots.Where(slot => slot.Domain == TargetSlotDomain.Game
                    && slot.MaterialSlotIndex is not null && slot.Material is not null)
                .GroupBy(slot => slot.MaterialSlotIndex!.Value).Select(group =>
                {
                    var exactMaterials = group.Select(slot => slot.Material!).DistinctBy(ObjectKey).ToList();
                    if (exactMaterials.Count != 1) return null;
                    var textures = group.Where(slot => slot.Input is TargetInputKind.BaseColor
                                or TargetInputKind.Normal or TargetInputKind.Rmo or TargetInputKind.Blend
                                or TargetInputKind.Ramp or TargetInputKind.Texture)
                        .SelectMany(slot => assetSourcesBySlot.GetValueOrDefault(slot.Id)
                            ?? new List<GameAssetRef>(), (slot, texture) =>
                            new LegacyResolvedTexture(slot.Input, texture.LogicalBundle,
                                texture.Name ?? $"Texture2D_{texture.PathId}", texture.PathId, Clone(texture),
                                slot.ShaderProperty))
                        .DistinctBy(texture => (texture.Input, texture.ShaderProperty,
                            ObjectKey(texture.Texture))).ToList();
                    return new LegacyResolvedMaterial(group.Key,
                        exactMaterials[0].Name ?? $"Material_{exactMaterials[0].PathId}",
                        Clone(exactMaterials[0]), textures);
                }).Where(material => material is not null).Cast<LegacyResolvedMaterial>().ToList();
            var tiers = slots.Where(slot => slot.Mesh is not null && slot.Tier is not null
                    && !string.Equals(slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(slot => slot.Tier, StringComparer.OrdinalIgnoreCase)
                .Select(slot => new LegacyResolvedTier(slot.Renderer.Name ?? slot.Part.RendererSlot,
                    slot.Tier!, Clone(slot.Renderer), Clone(slot.Mesh!))).ToList();
            return new LegacyResolvedPart(Clone(target), Clone(baseSlot.Renderer), Clone(baseSlot.Mesh),
                materials, tiers);
        };
    }

    private static string ObjectKey(GameAssetRef value) =>
        $"{value.GameBuild}\u001f{value.LogicalBundle}\u001f{value.PathId}";

    private static TargetPart Clone(TargetPart source) => new()
    {
        Subject = source.Subject, Outfit = source.Outfit, RendererSlot = source.RendererSlot,
    };

    private static GameAssetRef Clone(GameAssetRef source) => new()
    {
        GameBuild = source.GameBuild, LogicalBundle = source.LogicalBundle,
        PathId = source.PathId, Name = source.Name,
    };
}
