using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Model;
using Remold.Core.Project;

namespace Remold.Core.Workbench;

/// <summary>
/// Drops a whole picked subject (character · outfit) from the mod: its mesh targets, the textures solely
/// its, their <c>originals/</c> baselines, workspace files, Hide toggles, build-exclusions, and its
/// <see cref="SelectionEntry"/>. The ONE subject-scoped remove behind both entry points (Pick uncheck,
/// Edit header "Remove from mod"), driven entirely by the persisted project — no in-memory VM state.
///
/// <para><b>Texture ownership.</b> A texture is stored once per (bundle, object name) and shared by every
/// mesh that binds it (<see cref="ProjectTarget.Users"/> = owner mesh m_Names). A user belongs to the
/// removed subject when it is one of that subject's own mesh targets' names (authoritative — a
/// cross-prefix recovered part carries the BASE recipe-slot name and so does NOT match the alt's prefix)
/// OR it matches the subject's <see cref="Outfit.MeshPrefix"/> (catches a map-grain materialize with no
/// mesh target). A user a still-selected subject ALSO claims is kept, so a survivor's texture never loses
/// its user. A texture left with no users is deleted. Keep-on-unsure: untracked (null) users are kept, and
/// a survivor whose prefix can't be derived claims everything.</para>
///
/// <para>Every target under the subject is dropped whether or not it was edited;
/// <see cref="EditedFileCount"/> names only the subset the modder changed.</para>
/// </summary>
public static class SubjectRemoval
{
    /// <summary>Resolves a still-selected subject to its <see cref="Outfit.MeshPrefix"/>, or null when the
    /// roster can't derive it — null means "claims broadly" (keep-on-unsure). With no resolver, remaining
    /// subjects fall back to the conventional <c>c_&lt;stem&gt;_slg_</c> prefix.</summary>
    public delegate string? RemainingPrefixResolver(string character, string stem);

    /// <summary>Whether the subject has materialized content (mesh targets, or a texture solely its) — the
    /// trigger for the confirm dialog. A selection-only subject has none, so unchecking it silently removes
    /// just the ledger row.</summary>
    public static bool HasMaterializedContent(ModProject project, string character, string stem, string meshPrefix,
        RemainingPrefixResolver? remainingPrefix = null) =>
        Plan(project, character, stem, meshPrefix, remainingPrefix).AnyTargets;

    /// <summary>Count of EDITED workspace files this remove would drop, for the confirm dialog: the
    /// subject's edited mesh targets plus every edited texture solely its. A shared texture that survives
    /// isn't counted.</summary>
    public static int EditedFileCount(ModProject project, string character, string stem, string meshPrefix,
        RemainingPrefixResolver? remainingPrefix = null) =>
        Plan(project, character, stem, meshPrefix, remainingPrefix).EditedCount;

    /// <summary>Remove the subject: its mesh targets and originals, the textures solely its (dropping this
    /// subject's meshes from every shared texture's user set), its export folder, its
    /// <see cref="SelectionEntry"/>. Best-effort on the file system — a locked file leaves a stray, never
    /// throws. Idempotent.</summary>
    public static void Remove(ModProject project, string character, string stem, string meshPrefix,
        RemainingPrefixResolver? remainingPrefix = null)
    {
        var plan = Plan(project, character, stem, meshPrefix, remainingPrefix);

        // 1. mesh targets under the subject folder — target, workspace glb, original
        foreach (var t in plan.MeshTargets)
        {
            project.Targets.Remove(t);
            TryDeleteWorkspaceFile(project, t.ReplaceFile);
            TryDeleteWorkspaceFile(project, t.OriginalFile);
        }

        // 2. textures: drop users SOLELY this subject's; delete textures left with no users
        foreach (var t in project.Targets.Where(x => x.AssetType == "Texture2D").ToList())
        {
            if (t.Users is null) continue;   // unproven ownership — keep
            t.Users.RemoveAll(plan.IsSolelyRemovedUser);
            if (t.Users.Count == 0)
            {
                project.Targets.Remove(t);
                TryDeleteWorkspaceFile(project, t.ReplaceFile);
                TryDeleteWorkspaceFile(project, t.OriginalFile);
            }
        }

        // 3. the subject's Hide toggles, build-exclusions and toggle keys — none may survive to resurface
        // on a re-add
        project.RemoveSubjectHidden(character, stem);
        project.RemoveSubjectBuildExcluded(character, stem);
        project.RemoveSubjectChangeKeys(character, stem);

        // 4. sweep the export folder — untracked derived artifacts (UV guides, combined glb) live under it
        TryDeleteSubjectFolder(project, character, stem);

        // 5. the ledger row
        var entry = project.Selection.FirstOrDefault(s =>
            string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Outfit, stem, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) project.Selection.Remove(entry);
    }

    // ---- planning (shared by the confirm counts and the remove) ----

    private readonly record struct RemovePlan(
        List<ProjectTarget> MeshTargets, List<ProjectTarget> SolelyTextures, Predicate<string> IsSolelyRemovedUser,
        int EditedCount)
    {
        public bool AnyTargets => MeshTargets.Count > 0 || SolelyTextures.Count > 0;
    }

    private static RemovePlan Plan(ModProject project, string character, string stem, string meshPrefix,
        RemainingPrefixResolver? remainingPrefix)
    {
        // ownership is by PERSISTED IDENTITY; a mesh with no stamped (character, stem) belongs to no subject
        var meshes = project.Targets
            .Where(t => t.AssetType == "Mesh" && OwnedBy(t, character, stem))
            .ToList();

        // own mesh names are authoritative (a cross-prefix recovered part carries another outfit's slot name,
        // which prefix alone would miss); prefix is the map-grain fallback for a texture with no mesh target
        var removedNames = new HashSet<string>(meshes.Select(t => t.ObjectName), StringComparer.Ordinal);
        bool OwnedByRemoved(string user) =>
            removedNames.Contains(user) || (meshPrefix.Length > 0 && user.StartsWith(meshPrefix, StringComparison.OrdinalIgnoreCase));

        var remaining = project.Selection
            .Where(s => !(string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(s.Outfit, stem, StringComparison.OrdinalIgnoreCase)))
            .Select(s => BuildClaim(project, s.Character, s.Outfit, remainingPrefix))
            .ToList();
        bool ClaimedByRemaining(string user) => remaining.Any(c => c.Claims(user));

        bool IsSolelyRemoved(string user) => OwnedByRemoved(user) && !ClaimedByRemaining(user);

        // solely this subject's = tracked user set, every user one Remove would drop (same predicate as
        // Remove, so the confirm count can't drift from the act)
        var textures = project.Targets
            .Where(t => t.AssetType == "Texture2D"
                        && t.Users is { Count: > 0 }
                        && t.Users.All(IsSolelyRemoved))
            .ToList();
        // byte-compare (IsEdited), not the stored Edited flag: edit-then-revert reads unedited so the
        // confirm can't overstate; a target with no original on record reads as edited
        int editedCount = meshes.Count(project.IsEdited) + textures.Count(project.IsEdited);
        return new RemovePlan(meshes, textures, IsSolelyRemoved, editedCount);
    }

    /// <summary>A survivor's claim on shared-texture users: its own materialized mesh names plus its derived
    /// prefix. A null prefix claims every user — keep-on-unsure.</summary>
    private readonly record struct SubjectClaim(HashSet<string> Names, string? Prefix)
    {
        public bool Claims(string user) =>
            Prefix is null || Names.Contains(user)
            || (Prefix.Length > 0 && user.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static SubjectClaim BuildClaim(ModProject project, string character, string stem,
        RemainingPrefixResolver? remainingPrefix)
    {
        var names = new HashSet<string>(
            project.Targets.Where(t => t.AssetType == "Mesh" && OwnedBy(t, character, stem)).Select(t => t.ObjectName),
            StringComparer.Ordinal);
        // no resolver → conventional prefix; a resolver may return null (keep-on-unsure)
        var prefix = remainingPrefix is null ? $"c_{stem}_slg_" : remainingPrefix(character, stem);
        return new SubjectClaim(names, prefix);
    }

    /// <summary>Ownership by PERSISTED identity
    /// (<see cref="ProjectTarget.SubjectCharacter"/>/<see cref="ProjectTarget.SubjectOutfit"/>),
    /// case-insensitive to match the ledger. An unstamped target belongs to no subject — never guessed from
    /// its workspace path.</summary>
    private static bool OwnedBy(ProjectTarget t, string character, string stem) =>
        t.SubjectCharacter is not null && t.SubjectOutfit is not null
        && string.Equals(t.SubjectCharacter, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.SubjectOutfit, stem, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteWorkspaceFile(ModProject project, string? rel)
    {
        if (rel is null || project.RootDir is null) return;
        try { var p = project.Resolve(rel); if (File.Exists(p)) File.Delete(p); }
        catch { /* best-effort — a locked file leaves a stray, never a throw */ }
    }

    private static void TryDeleteSubjectFolder(ModProject project, string character, string stem)
    {
        if (project.RootDir is null) return;
        try
        {
            var dir = Path.Combine(project.RootDir, Materializer.SubjectFolder(character, stem));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }
}
