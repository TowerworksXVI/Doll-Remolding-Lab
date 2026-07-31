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
    /// call <see cref="Derive"/>. Pure: nothing here writes to the project, so a texture edit a replacement
    /// could have taken over is a WARNING here, never a change made on the way past.</summary>
    public static List<MeshEdit> DeriveAll(ModProject project,
        Func<string, string, SubjectModel?> resolveSubject, IList<string> warnings)
    {
        var edits = new List<MeshEdit>();
        // One REPLACE per physical mesh across ALL subjects; the rule and its key live in ReplaceClaims,
        // which the edit-time adoption asks too. A collision WARNS naming both subjects and does not mark
        // the part replaced for this subject, so the loss is visible and this subject's texture edits are
        // accounted for rather than swallowed. Hide/Retexture pass through per subject; ModBuilder dedupes
        // those by mesh CONTENT (ib hash), since same-named parts across outfits can carry different bytes.
        var replaceClaims = ReplaceClaims.Of(project);
        foreach (var sel in project.Selection)
        {
            string subject = ReplaceClaims.SubjectLabel(sel.Character, sel.Outfit);
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
                if (!partsByName.TryGetValue(t.ObjectName, out var editedPart))
                    throw new InvalidOperationException(
                        $"edited mesh '{t.ObjectName}' is not in {sel.Character} · {sel.Outfit}'s roster (stale after a game update?)");
                if (IsHiddenMesh(t.ObjectName))
                {
                    warnings.Add($"'{editedPart.Token}' is hidden. Its mesh edit is not in this build");
                    continue;
                }
                if (!replaceClaims.Ships(t))
                {
                    var holder = replaceClaims.HolderOf(t.ObjectName, t.PathId) ?? subject;
                    claimedElsewhere[t.ObjectName] = holder;
                    warnings.Add($"'{editedPart.Token}' is replaced on both {holder} and {subject}. One "
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

            bool BindsAnEditedTexture(SubjectPart p) =>
                p.Materials.Any(m => m.Maps.Any(map => editedTextures.Any(x => Binds(x, map))));

            // Every (donor slot, edited texture) pair the part binds on a slot the Replace rebinds and whose
            // file no donor row of that replacement names — the edits a build drops. One pair per texture and
            // slot: a map dressing several of the part's material slots is one adoption over all of them.
            //
            // Which shader slots those are is DonorMapBinding's answer, the same one the edit-time adoption
            // and the map cards read, so a slot that adopts is never one this pass calls un-emitted. Base
            // colour therefore covers _MainTex as well as _BaseMap; no material in the measured GFL2 corpus
            // binds _MainTex, so the two readings agree on every subject the game ships.
            List<(DonorMapSlot Slot, ProjectTarget Texture)> ReboundEdits(SubjectPart p, ProjectTarget mesh)
            {
                var found = new List<(DonorMapSlot Slot, ProjectTarget Texture)>();
                foreach (var map in p.Materials.SelectMany(m => m.Maps))
                {
                    if (DonorMapBinding.DonorSlotOf(map.Slot) is not { } slot) continue;
                    foreach (var x in editedTextures)
                    {
                        if (!Binds(x, map) || TextureAdoptions.ReplacementCarries(mesh, x)) continue;
                        if (!found.Any(f => f.Slot == slot && ReferenceEquals(f.Texture, x)))
                            found.Add((slot, x));
                    }
                }
                return found;
            }

            foreach (var p in model.Parts)
            {
                // A Replace rebinds the slots its donor textures carry — the ones DonorMapBinding maps to a
                // donor slot, and only those; every other slot on the part's materials keeps drawing the
                // game texture. So the replacement takes those away from a texture edit and leaves the rest
                // to it. An edit one of the replacement's donor maps carries ships WITH the Replace; a
                // rebound-slot edit no donor row references is one the adoption could not take, and is
                // warned about with its reason.
                bool replaced = replacedMeshes.Contains(p.SlotName);
                if (replaced)
                {
                    var mesh = replacedTargets[p.SlotName];
                    var stray = ReboundEdits(p, mesh);
                    if (stray.Count > 0)
                    {
                        var bound = DonorMapBinding.BoundSubmeshesByMap(p.Materials);
                        foreach (var (slot, texture) in stray)
                        {
                            var all = bound.TryGetValue((slot, texture.ObjectName, texture.Bundle), out var b)
                                ? b : (IReadOnlyList<int>)Array.Empty<int>();
                            var (inRange, landing) =
                                TextureAdoptions.Landing(mesh, slot, texture.ReplaceFile, all);
                            warnings.Add(StrayEditWarning(p.Token, mesh, slot, inRange, landing.Count > 0));
                        }
                    }
                }
                // the part another subject's Replace already claimed: this build ships one replacement for
                // that mesh, so a texture edit here has no draw of its own left to land on either
                if (claimedElsewhere.TryGetValue(p.SlotName, out var claimant))
                {
                    if (BindsAnEditedTexture(p))
                        warnings.Add($"'{p.Token}' is replaced by {claimant}. "
                            + "Its texture edit is not in this build");
                    continue;
                }
                // a hidden part draws nothing, so its texture edit ships nothing
                if (IsHiddenMesh(p.SlotName))
                {
                    if (BindsAnEditedTexture(p))
                        warnings.Add($"'{p.Token}' is hidden. Its texture edit is not in this build");
                    continue;
                }
                var perSubmesh = new Dictionary<int, SubmeshTextures>();
                for (int mi = 0; mi < p.Materials.Count; mi++)
                {
                    foreach (var map in p.Materials[mi].Maps)
                    {
                        var t = editedTextures.FirstOrDefault(x => Binds(x, map));
                        if (t is null) continue;
                        if (DonorMapBinding.DonorSlotOf(map.Slot) is not { } slot)
                        {
                            warnings.Add($"'{map.TextureName}' binds as {map.Slot} on '{p.Token}'. "
                                + "That slot isn't emitted yet; the edit doesn't show on this mesh");
                            continue;
                        }
                        // the slots the replacement rebound are already accounted for above
                        if (replaced) continue;
                        if (!perSubmesh.TryGetValue(mi, out var st))
                            perSubmesh[mi] = st = new SubmeshTextures { Submesh = mi };
                        switch (slot)
                        {
                            case DonorMapSlot.BaseColor: st.Albedo = t.ReplaceFile; break;
                            case DonorMapSlot.Normal: st.Normal = t.ReplaceFile; break;
                            default: st.Rmo = t.ReplaceFile; break;
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

    /// <summary>What a texture edit the replacement can't take over says: why it isn't in the build, and
    /// what to do about it. Three states reach here — a map dressing only submeshes the replacement doesn't
    /// have, a map whose every landing slot the modder already spoke for, and one with a free slot that the
    /// edit-time adoption never saw (an edit made with no subject model in hand). Each names its own way
    /// out, the free-slot one by the two gestures that re-run the adoption.</summary>
    /// <param name="part">The part token the Edit tree labels the replacement by, so a warning and the
    /// change row above it name one part once.</param>
    /// <param name="inRange">The submeshes the map dresses that the replacement has at all.</param>
    /// <param name="adoptable">A submesh is free to take the map, so there is a landing and no reason to
    /// name.</param>
    static string StrayEditWarning(string part, ProjectTarget mesh, DonorMapSlot slot,
        IReadOnlyList<int> inRange, bool adoptable)
    {
        if (inRange.Count == 0)
            return $"'{part}' is replaced. This map dresses submeshes {part}'s replacement doesn't have. "
                + $"Send {part} back from Blender to add them";
        if (adoptable)
            return $"'{part}' is replaced. Its texture edit is not in this build. Save the texture again in "
                + "② Edit, or drop the edited image on the part's map card";
        return $"'{part}' is replaced. Its replacement {TextureAdoptions.Holder(mesh, slot, inRange)}, so "
            + "the texture edit is not in this build. Drop the edited image on the part's map card to use "
            + "it instead";
    }

    /// <summary>Whether a materialized texture target IS the one a material's map binds. Identity is the
    /// (name, bundle) pair the game carries the asset under: same-named textures in different bundles are
    /// distinct assets.</summary>
    static bool Binds(ProjectTarget texture, SubjectMap map) =>
        string.Equals(texture.ObjectName, map.TextureName, StringComparison.Ordinal)
        && string.Equals(texture.Bundle, map.BundleId, StringComparison.Ordinal);

    static bool OwnedBy(ProjectTarget t, string character, string stem) =>
        t.SubjectCharacter is not null && t.SubjectOutfit is not null
        && string.Equals(t.SubjectCharacter, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.SubjectOutfit, stem, StringComparison.OrdinalIgnoreCase);
}
