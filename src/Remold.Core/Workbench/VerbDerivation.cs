using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;

namespace Remold.Core.Workbench;

/// <summary>
/// Derives the build's edit list from workbench state — verbs are never authored or persisted, so what
/// builds can't drift from the Edit pane. Per selected subject:
/// <list type="bullet">
/// <item>a mesh target differing from its original (<see cref="ModProject.IsEdited"/>) is a
/// <b>Replace</b> — the workspace glb IS the donor, its <see cref="ProjectTarget.DonorTextures"/> the
/// per-donor-submesh maps;</item>
/// <item>an edited texture target becomes a <b>Retexture</b> on each roster part binding it; a replaced
/// or hidden part doesn't retexture — its vanilla draws are gone;</item>
/// <item>a <see cref="ModProject.Hidden"/> entry is a <b>Hide</b> and wins over edits on the same mesh
/// (the edit stays in the workspace for when it's unhidden).</item>
/// </list>
/// Throws on an EDITED mesh target that no longer matches the live roster, and on a subject that carries
/// content but doesn't resolve. A stale Hidden entry only warns.
/// </summary>
public static class VerbDerivation
{
    /// <summary>The edit list a build ships: <see cref="DeriveAll"/> minus what the Build pane unticked
    /// (<see cref="ModProject.BuildExcluded"/>), matched on VERB as well as mesh — an exclusion belongs to
    /// the derived edit it was ticked against, not to the mesh. Exclusion is the LAST pass, so unticking
    /// one row removes exactly that row. Silent by design.</summary>
    public static List<MeshEdit> Derive(ModProject project,
        Func<string, string, SubjectModel?> resolveSubject, IList<string> warnings) =>
        DeriveAll(project, resolveSubject, warnings)
            .Where(e => !project.IsBuildExcluded(e.Character, e.Outfit, e.Mesh, e.Verb))
            .ToList();

    /// <summary>Every derived edit, build-excluded ones included — the Build pane's change list. Builds
    /// call <see cref="Derive"/>.</summary>
    public static List<MeshEdit> DeriveAll(ModProject project,
        Func<string, string, SubjectModel?> resolveSubject, IList<string> warnings)
    {
        var edits = new List<MeshEdit>();
        // one REPLACE per physical mesh across ALL subjects — outfits share meshes and two pipelines
        // capturing the same draw would fight. Hide/Retexture pass through per subject; ModBuilder dedupes
        // those by mesh CONTENT (ib hash), since same-named parts across outfits can carry different bytes.
        //
        // The claim is keyed on NAME and path id, which is all this pass knows: no mesh bytes are read here
        // and an address-resolved part carries no path id. The key is therefore coarser than identity, and
        // two subjects whose parts merely share a name collide on it. A collision WARNS naming both subjects
        // and does not mark the part replaced for this subject, so the loss is visible and this subject's
        // texture edits are accounted for rather than swallowed. Whether it really was one mesh is settled
        // at build time against the hashes (see Migoto.ModBuilder), which is where the identity lives.
        var replaceClaims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // the subject already holding this mesh's Replace, or null when this call takes the claim
        string? ClaimReplace(string mesh, long? pathId, string subject)
        {
            string key = mesh + "\0" + pathId;
            if (replaceClaims.TryGetValue(key, out var holder)) return holder;
            replaceClaims[key] = subject;
            return null;
        }
        foreach (var sel in project.Selection)
        {
            string subject = $"{sel.Character} · {sel.Outfit}";
            var meshTargets = project.Targets
                .Where(t => t.AssetType == "Mesh" && OwnedBy(t, sel.Character, sel.Outfit))
                .ToList();
            var hidden = project.Hidden.Where(h => h.IsForSubject(sel.Character, sel.Outfit)).ToList();
            var editedMeshes = meshTargets
                .Where(t => project.IsTargetPresent(t) && project.IsEdited(t))
                .ToList();

            var model = resolveSubject(sel.Character, sel.Outfit);
            if (model is null)
            {
                if (editedMeshes.Count > 0 || hidden.Count > 0)
                    throw new InvalidOperationException(
                        $"subject '{sel.Character} · {sel.Outfit}' didn't resolve. Re-check against the current game install");
                continue;   // selection-only subject: nothing to derive
            }

            var partsByName = new Dictionary<string, SubjectPart>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in model.Parts) partsByName[p.SlotName] = p;

            bool IsHiddenMesh(string mesh) => hidden.Any(h =>
                string.Equals(h.Mesh, mesh, StringComparison.OrdinalIgnoreCase));

            // hides: a stale entry (mesh gone after an update) only warns
            foreach (var h in hidden)
            {
                if (!partsByName.ContainsKey(h.Mesh))
                {
                    warnings.Add($"hidden mesh '{h.Mesh}' is not in {sel.Character} · {sel.Outfit}'s roster. Ignored");
                    continue;
                }
                edits.Add(new MeshEdit
                {
                    Character = sel.Character, Outfit = sel.Outfit, Mesh = h.Mesh,
                    Verb = EditVerbs.Hide,
                });
            }

            // replaces: every edited mesh target; the workspace glb is the donor
            var replacedMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // slot name -> the target whose Replace ships, for asking what its donor rows carry
            var replacedTargets = new Dictionary<string, ProjectTarget>(StringComparer.OrdinalIgnoreCase);
            // slot name -> the subject whose Replace already claimed it, for the ones dropped here
            var claimedElsewhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in editedMeshes)
            {
                if (!partsByName.ContainsKey(t.ObjectName))
                    throw new InvalidOperationException(
                        $"edited mesh '{t.ObjectName}' is not in {sel.Character} · {sel.Outfit}'s roster (stale after a game update?)");
                if (IsHiddenMesh(t.ObjectName))
                {
                    warnings.Add($"'{t.ObjectName}' is hidden. Its mesh edit is not in this build");
                    continue;
                }
                if (ClaimReplace(t.ObjectName, t.PathId, subject) is { } holder)
                {
                    claimedElsewhere[t.ObjectName] = holder;
                    warnings.Add($"'{t.ObjectName}' is replaced on both {holder} and {subject}. One "
                        + $"replacement per part name ships, so {subject}'s is not in this build. Build the "
                        + "two subjects as separate mods");
                    continue;
                }
                replacedMeshes.Add(t.ObjectName);
                replacedTargets[t.ObjectName] = t;
                edits.Add(new MeshEdit
                {
                    Character = sel.Character, Outfit = sel.Outfit, Mesh = t.ObjectName,
                    PathId = t.PathId,
                    Verb = EditVerbs.Replace,
                    DonorFile = t.ReplaceFile,
                    Textures = t.DonorTextures is { Count: > 0 } ? t.DonorTextures : null,
                    BakedRest = t.BakedRest is { Count: > 0 } ? t.BakedRest : null,
                });
            }

            // retextures: THIS SUBJECT'S edited textures mapped through each part's materials (order IS the
            // submesh binding). Ownership is the scope: an edit made on one outfit's file ships on that
            // outfit alone, exactly like mesh targets above.
            var editedTextures = project.Targets
                .Where(t => t.AssetType == "Texture2D" && OwnedBy(t, sel.Character, sel.Outfit)
                    && project.IsTargetPresent(t) && project.IsEdited(t))
                .ToList();
            if (editedTextures.Count == 0) continue;

            bool BindsAnEditedTexture(SubjectPart p, Func<string, bool>? onSlot = null,
                Func<ProjectTarget, bool>? where = null) =>
                p.Materials.Any(m => m.Maps.Any(map =>
                    (onSlot is null || onSlot(map.Slot))
                    && editedTextures.Any(x =>
                        (where is null || where(x))
                        && string.Equals(x.ObjectName, map.TextureName, StringComparison.Ordinal)
                        && string.Equals(x.Bundle, map.BundleId, StringComparison.Ordinal))));

            foreach (var p in model.Parts)
            {
                // A Replace rebinds the three slots its donor textures carry, and only those; every other
                // slot on the part's materials keeps drawing the game texture. So the replacement takes the
                // three away from a texture edit and leaves the rest to it. An edit the send-back adopted
                // as one of the replacement's own donor maps ships WITH the Replace; only a rebound-slot
                // edit no donor row references is lost — and said.
                bool replaced = replacedMeshes.Contains(p.SlotName);
                if (replaced && BindsAnEditedTexture(p, ReboundByAReplace,
                        x => !ReplacementCarries(replacedTargets[p.SlotName], x)))
                    warnings.Add($"'{p.SlotName}' is replaced. Its texture edit is not in this build");
                // the part another subject's Replace already claimed: this build ships one replacement for
                // that mesh, so a texture edit here has no draw of its own left to land on either
                if (claimedElsewhere.TryGetValue(p.SlotName, out var claimant))
                {
                    if (BindsAnEditedTexture(p))
                        warnings.Add($"'{p.SlotName}' is replaced by {claimant}. "
                            + "Its texture edit is not in this build");
                    continue;
                }
                // a hidden part draws nothing, so its texture edit ships nothing
                if (IsHiddenMesh(p.SlotName))
                {
                    if (BindsAnEditedTexture(p))
                        warnings.Add($"'{p.SlotName}' is hidden. Its texture edit is not in this build");
                    continue;
                }
                var perSubmesh = new Dictionary<int, SubmeshTextures>();
                for (int mi = 0; mi < p.Materials.Count; mi++)
                {
                    foreach (var map in p.Materials[mi].Maps)
                    {
                        var t = editedTextures.FirstOrDefault(x =>
                            string.Equals(x.ObjectName, map.TextureName, StringComparison.Ordinal)
                            && string.Equals(x.Bundle, map.BundleId, StringComparison.Ordinal));
                        if (t is null) continue;
                        // the three the replacement rebound are already accounted for above
                        if (replaced && ReboundByAReplace(map.Slot)) continue;
                        if (!perSubmesh.TryGetValue(mi, out var st))
                            perSubmesh[mi] = st = new SubmeshTextures { Submesh = mi };
                        switch (map.Slot)
                        {
                            case "_BaseMap": st.Albedo = t.ReplaceFile; break;
                            case "_BumpMap": st.Normal = t.ReplaceFile; break;
                            case "_RMOTex": st.Rmo = t.ReplaceFile; break;
                            default:
                                warnings.Add($"'{map.TextureName}' binds as {map.Slot} on '{p.SlotName}'. "
                                    + "That slot isn't emitted yet; the edit doesn't show on this mesh");
                                break;
                        }
                    }
                }
                var sets = perSubmesh.Values.Where(s => s.Albedo != null || s.Normal != null || s.Rmo != null)
                    .OrderBy(s => s.Submesh).ToList();
                if (sets.Count == 0) continue;
                edits.Add(new MeshEdit
                {
                    Character = sel.Character, Outfit = sel.Outfit, Mesh = p.SlotName,
                    Verb = EditVerbs.Retexture,
                    Textures = sets,
                });
            }
        }
        return edits;
    }

    /// <summary>The slots a <see cref="EditVerbs.Replace"/> rebinds on its own submeshes — the three a
    /// donor texture set carries, which are the same three the retexture emitter has cases for. A material's
    /// other slots are untouched by a replacement and keep drawing the game texture.</summary>
    static bool ReboundByAReplace(string slot) => slot is "_BaseMap" or "_BumpMap" or "_RMOTex";

    /// <summary>Whether the replacement's donor rows reference this texture's workspace file. The send-back
    /// records a retextured own map as the replacement's donor map, and a row carrying it means the edit
    /// ships with the Replace.</summary>
    static bool ReplacementCarries(ProjectTarget mesh, ProjectTarget texture) =>
        mesh.DonorTextures is { Count: > 0 } rows && rows.Any(r =>
            PathEq(r.Albedo, texture.ReplaceFile) || PathEq(r.Normal, texture.ReplaceFile)
            || PathEq(r.Rmo, texture.ReplaceFile));

    static bool PathEq(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool OwnedBy(ProjectTarget t, string character, string stem) =>
        t.SubjectCharacter is not null && t.SubjectOutfit is not null
        && string.Equals(t.SubjectCharacter, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.SubjectOutfit, stem, StringComparison.OrdinalIgnoreCase);
}
