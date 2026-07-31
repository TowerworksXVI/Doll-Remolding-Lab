using System;
using System.Collections.Generic;
using Remold.Core.Project;

namespace Remold.Core.Workbench;

/// <summary>Which <see cref="EditVerbs.Replace"/> ships for one physical mesh. A build carries ONE
/// replacement per mesh across every selected subject — outfits share meshes and two pipelines capturing the
/// same draw would fight — so a mesh two subjects both replace is claimed by the first in selection order and
/// the rest are dropped.
///
/// <para>The claim is keyed on mesh NAME and path id, which is all a pass over the project knows: no mesh
/// bytes are read here and an address-resolved part carries no path id. The key is therefore coarser than
/// identity, and two subjects whose parts merely share a name collide on it. Whether it really was one mesh
/// is settled at build time against the hashes (see <c>Migoto.ModBuilder</c>), which is where identity
/// lives.</para>
///
/// <para>ONE home for the rule: <see cref="VerbDerivation"/> drops the losing subject's Replace and warns,
/// and <see cref="TextureAdoptions"/> refuses to adopt onto a replacement the build won't ship. Both ask
/// here, so the edit-time answer and the build's own cannot disagree.</para></summary>
public sealed class ReplaceClaims
{
    /// <summary>The winning target per (mesh name, path id), with the subject label that owns it.</summary>
    private readonly Dictionary<string, (ProjectTarget Target, string Subject)> _won =
        new(StringComparer.OrdinalIgnoreCase);

    private ReplaceClaims() { }

    /// <summary>How a subject reads in a warning that names two of them.</summary>
    public static string SubjectLabel(string character, string outfit) => $"{character} · {outfit}";

    /// <summary>The claims the project as it stands would settle: selection order decides, and within a
    /// subject the target order does. A HIDDEN mesh claims nothing — the hide wins over the edit, so that
    /// subject ships no replacement for it and the mesh is free for another subject to claim.</summary>
    public static ReplaceClaims Of(ModProject project)
    {
        var claims = new ReplaceClaims();
        foreach (var sel in project.Selection)
        {
            string subject = SubjectLabel(sel.Character, sel.Outfit);
            foreach (var t in project.Targets)
            {
                if (t.AssetType != "Mesh" || !OwnedBy(t, sel.Character, sel.Outfit)) continue;
                if (!project.IsTargetPresent(t) || !project.IsEdited(t)) continue;
                if (project.IsHidden(sel.Character, sel.Outfit, t.ObjectName)) continue;
                string key = t.ObjectName + "\0" + t.PathId;
                if (!claims._won.ContainsKey(key)) claims._won[key] = (t, subject);
            }
        }
        return claims;
    }

    /// <summary>Whether THIS target is the one whose Replace ships. False for a target another subject's
    /// claim beat, and for one no claim covers at all (a hidden or unedited mesh).</summary>
    public bool Ships(ProjectTarget meshTarget) =>
        _won.TryGetValue(meshTarget.ObjectName + "\0" + meshTarget.PathId, out var won)
        && ReferenceEquals(won.Target, meshTarget);

    /// <summary>The subject whose Replace ships for this mesh, or null when nothing claims it.</summary>
    public string? HolderOf(string mesh, long? pathId) =>
        _won.TryGetValue(mesh + "\0" + pathId, out var won) ? won.Subject : null;

    private static bool OwnedBy(ProjectTarget t, string character, string stem) =>
        t.SubjectCharacter is not null && t.SubjectOutfit is not null
        && string.Equals(t.SubjectCharacter, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.SubjectOutfit, stem, StringComparison.OrdinalIgnoreCase);
}
