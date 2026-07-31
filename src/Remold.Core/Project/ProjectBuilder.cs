using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Export;

namespace Remold.Core.Project;

/// <summary>
/// Bridges a finished export (<see cref="ExportReport"/>) into the persistent <see cref="ModProject"/>:
/// one selection entry per outfit plus one target per Ok exported file, with workspace-relative paths,
/// persisted originals, and the original vertex count. Pure mapping, no I/O.
///
/// <b>Additive</b>: re-callable as the modder exports more parts. The selection entry dedupes on
/// character+stem and an already-tracked replace file is skipped, so an incremental export never
/// clobbers an already-exported (possibly edited) part.
/// </summary>
public static class ProjectBuilder
{
    public static void AddExport(ModProject proj, ExportReport report, string rootDir,
        string character, string stem)
    {
        // exactly one row per (character, stem); parts are NOT unioned onto the entry — the touched set
        // is the target list, and an outfit's exported parts derive from its mesh targets
        var entry = proj.Selection.FirstOrDefault(s =>
            string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Outfit, stem, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            proj.Selection.Add(new SelectionEntry { Character = character, Outfit = stem });

        // stamps the subject identity so a subject-scoped remove owns targets by identity, not by folder
        AddTargets(proj, report, rootDir, character, stem);
    }

    private static void AddTargets(ModProject proj, ExportReport report, string rootDir,
        string? subjectCharacter, string? subjectStem)
    {
        foreach (var f in report.Files.Where(f => f.Ok))
        {
            var rel = Rel(rootDir, f.Path);
            // idempotent — either key is a hit:
            //   • same replace_file (a shared texture re-encountered per using part / later pass);
            //   • same bundle+object identity under a DIFFERENT file (an imported target vs a fresh export
            //     of the same object); without this both ship, targeting one bundle+object twice.
            var existing = proj.Targets.FirstOrDefault(x =>
                string.Equals(x.ReplaceFile, rel, StringComparison.OrdinalIgnoreCase)
                || SameObject(x, f, subjectCharacter, subjectStem));
            if (existing is not null)
            {
                if (existing.AssetType == "Texture2D")
                {
                    if (f.Users is { Count: > 0 })
                        foreach (var u in f.Users)
                            if (!(existing.Users ??= new()).Contains(u, StringComparer.Ordinal))
                                existing.Users.Add(u);
                    existing.Source = f.Source;
                }
                continue;
            }
            var t = new ProjectTarget
            {
                AssetType = f.Kind == "mesh" ? "Mesh" : "Texture2D",
                Bundle = f.Bundle ?? "",
                ObjectName = f.AssetName,
                // exact selector on smr-body meshes (enemy bundles hold same-named copies)
                PathId = f.PathId,
                ReplaceFile = rel,
                Users = f.Kind == "texture" ? f.Users?.Distinct(StringComparer.Ordinal).ToList() : null,   // meshes that use it (Edit nesting + badge)
                Source = f.Kind == "texture" ? f.Source : null,
                Edited = false,
            };
            // both kinds persist a pristine original: it is what Revert copies back, and what the
            // byte-compare reads to tell an edited file from an untouched one
            if (f.OriginalPath is not null) t.OriginalFile = Rel(rootDir, f.OriginalPath);
            // BOTH kinds are subject-owned: the subject an edit belongs to is what the build scopes to
            t.SubjectCharacter = subjectCharacter;
            t.SubjectOutfit = subjectStem;
            if (f.Kind == "mesh")
            {
                if (report.OriginalMeshByPath.TryGetValue(f.Path, out var om)) t.OriginalVerts = om.VertexCount;
                // the part's other LOD-tier slots, so the build can fan the one edit into them all
                if (f.LodSiblings is { Count: > 0 }) t.LodSlots = f.LodSiblings.ToList();
                // the scene-rest uprighting baked into the glb — the build must un-bake by this
                if (f.BakedRest is { Count: > 0 }) t.BakedRest = f.BakedRest.ToList();
            }
            proj.Targets.Add(t);
        }
    }

    /// <summary>Do an existing target and a freshly-exported file target the SAME thing? Matched on
    /// bundle + object identity: object_name, plus path_id where the export selects by id. Empty or
    /// absent identity on either side is never a match, so a path_id-only target and a named export stay
    /// separate — they select different ways.
    ///
    /// <para>On a path-id-selected mesh the NAME is still half the key, because a mesh target's object name
    /// is its renderer SLOT's: a prefab can point two slots at one mesh object, and those are two parts of
    /// the subject with two workspace files and two edits. Keying on the path id alone would fold the second
    /// slot into the first target and leave that part with nothing staged under its own name.</para></summary>
    private static bool SameObject(ProjectTarget existing, ExportedFile f,
        string? subjectCharacter, string? subjectStem)
    {
        if (string.IsNullOrEmpty(f.Bundle)) return false;
        // BOTH kinds dedupe per SUBJECT: subjects genuinely share game objects (palette variants, faction
        // weapons, one texture worn by several outfits) and every workbench flow is subject-scoped, so the
        // second subject lands its OWN target — its own workspace file, its own edit, its own build scope.
        if ((existing.AssetType == "Mesh") != (f.Kind == "mesh")) return false;
        if (!string.Equals(existing.SubjectCharacter, subjectCharacter, StringComparison.Ordinal)
            || !string.Equals(existing.SubjectOutfit, subjectStem, StringComparison.Ordinal)) return false;
        if (!string.Equals(existing.Bundle, f.Bundle, StringComparison.OrdinalIgnoreCase)) return false;
        // a path-id-selected file's identity is (bundle, path_id, slot name): same-named copies in one enemy
        // bundle are DISTINCT objects, and two slots on ONE object are distinct parts
        if (f.PathId is { } pid) return existing.PathId == pid && SameName(existing, f);
        if (existing.PathId is not null) return false;
        return SameName(existing, f);
    }

    private static bool SameName(ProjectTarget existing, ExportedFile f) =>
        !string.IsNullOrEmpty(f.AssetName)
        && !string.IsNullOrEmpty(existing.ObjectName)
        && string.Equals(existing.ObjectName, f.AssetName, StringComparison.Ordinal);

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
