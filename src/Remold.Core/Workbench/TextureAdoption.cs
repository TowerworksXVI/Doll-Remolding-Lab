using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;
using Remold.Core.Textures;

namespace Remold.Core.Workbench;

/// <summary>One edited texture a replacement can take over as its own map: the edit binds a slot the
/// <see cref="EditVerbs.Replace"/> on <paramref name="Mesh"/> rebinds, and no donor row of that replacement
/// names its workspace file, so a build would drop it. <see cref="TextureAdoptions.CandidatesFor"/> finds
/// these; <see cref="TextureAdoptions.Apply"/> is what writes them.</summary>
/// <param name="Mesh">The replaced part's mesh target — the record the donor rows live on.</param>
/// <param name="Texture">The edited texture target whose workspace PNG the replacement takes.</param>
/// <param name="Slot">Which of the three donor slots the map ships as.</param>
/// <param name="Submeshes">Every donor submesh the map dresses, already inside the donor's own submesh count
/// and already clear of the slots the modder spoke for.</param>
/// <param name="Part">The part token the Edit tree labels the replacement by — what the status line names,
/// since the mesh's slot name is a game identifier the modder never chose.</param>
public sealed record TextureAdoption(ProjectTarget Mesh, ProjectTarget Texture, DonorMapSlot Slot,
    IReadOnlyList<int> Submeshes, string Part);

/// <summary>A rebound slot an adoption could not take because the modder already spoke for it, named the
/// way the Edit pane names things: the part's own token and what the slot holds.</summary>
/// <param name="Part">The part token the Edit tree labels the replacement by.</param>
/// <param name="Holder">What the slot already answers with, as <see cref="TextureAdoptions.Holder"/>
/// phrases it.</param>
public sealed record AdoptionBlocked(string Part, string Holder);

/// <summary>Writing adoptions onto their replacements. The record is a REFERENCE to the texture's workspace
/// PNG — the same form <see cref="DonorTextureIntake"/> gives a map a Blender session returned untouched
/// with an edit behind it — so nothing is copied and no file is written: the edit ships with the Replace
/// because the donor row names the very file the modder edits, and the part's Revert drops it with the mesh
/// edit like any other donor map.</summary>
public static class TextureAdoptions
{
    /// <summary>Every adoption ONE edited texture opens on ONE subject: for each of that subject's replaced
    /// parts binding the texture on a slot the replacement rebinds, the donor submeshes the map dresses that
    /// are free to take it. A part whose replacement already names the file is not a candidate — the edit
    /// already ships with it — and neither is a hidden part, whose replacement draws nothing.
    /// <para><paramref name="blocked"/> receives the slots that had somewhere to land and were held by an
    /// ask of the modder's own, so a caller can say the edit stays plain and name what holds the slot. A
    /// slot the replacement has no submesh for reports nothing here: the build's own warning names that
    /// remedy. So does a part whose Replace another subject's <see cref="ReplaceClaims"/> claim beat — that
    /// replacement never ships, so there is nothing to adopt onto and nothing to say about it here.</para>
    /// </summary>
    public static IReadOnlyList<TextureAdoption> CandidatesFor(ModProject project, SubjectModel model,
        ProjectTarget texture, IList<AdoptionBlocked>? blocked = null)
    {
        var found = new List<TextureAdoption>();
        if (texture.AssetType != "Texture2D" || !project.IsTargetPresent(texture) || !project.IsEdited(texture))
            return found;
        var claims = ReplaceClaims.Of(project);
        foreach (var part in model.Parts)
        {
            if (project.IsHidden(model.Character, model.Stem, part.SlotName)) continue;
            if (ReplacedTarget(project, model, part.SlotName) is not { } mesh) continue;
            if (!claims.Ships(mesh)) continue;
            if (ReplacementCarries(mesh, texture)) continue;
            var bound = DonorMapBinding.BoundSubmeshesByMap(part.Materials);
            foreach (var slot in ReboundSlots(part, texture))
            {
                var all = bound.TryGetValue((slot, texture.ObjectName, texture.Bundle), out var b)
                    ? b : (IReadOnlyList<int>)Array.Empty<int>();
                var (inRange, landing) = Landing(mesh, slot, texture.ReplaceFile, all);
                if (landing.Count > 0)
                { found.Add(new TextureAdoption(mesh, texture, slot, landing, part.Token)); continue; }
                if (inRange.Count > 0) blocked?.Add(new AdoptionBlocked(part.Token, Holder(mesh, slot, inRange)));
            }
        }
        return found;
    }

    /// <summary>Where one edited map can land on one replacement: the submeshes it dresses that the
    /// replacement HAS, and of those the ones whose slot is still free. Both halves answer the derivation's
    /// warning as well as the adoption — a map with nothing in range is a different loss from one whose
    /// every landing slot is spoken for.</summary>
    /// <param name="bound">The submeshes the map dresses, as the material binding reports them.</param>
    public static (IReadOnlyList<int> InRange, IReadOnlyList<int> Landing) Landing(ProjectTarget mesh,
        DonorMapSlot slot, string? file, IReadOnlyList<int> bound)
    {
        // Past the replacement's own submesh count there is no donor row to carry a map, the same range the
        // map-card drop refuses outside of.
        int donorSubmeshes = mesh.DonorMaterials?.Count ?? 0;
        var inRange = bound.Where(i => i >= 0 && i < donorSubmeshes).ToList();
        return (inRange, inRange.Where(i => SlotIsFree(mesh, i, slot, file)).ToList());
    }

    /// <summary>The subject's mesh target for one part slot when a <see cref="EditVerbs.Replace"/> would
    /// ship for it, else null.</summary>
    private static ProjectTarget? ReplacedTarget(ModProject project, SubjectModel model, string slotName) =>
        project.Targets.FirstOrDefault(t => t.AssetType == "Mesh"
            && string.Equals(t.ObjectName, slotName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.SubjectCharacter, model.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.SubjectOutfit, model.Stem, StringComparison.OrdinalIgnoreCase)
            && project.IsTargetPresent(t) && project.IsEdited(t));

    /// <summary>The donor slots a part binds this texture on that a Replace rebinds, each once — a map
    /// dressing several of the part's material slots is one adoption over all of them.</summary>
    private static IEnumerable<DonorMapSlot> ReboundSlots(SubjectPart part, ProjectTarget texture)
    {
        var seen = new List<DonorMapSlot>();
        foreach (var map in part.Materials.SelectMany(m => m.Maps))
        {
            if (DonorMapBinding.DonorSlotOf(map.Slot) is not { } slot || seen.Contains(slot)) continue;
            if (!string.Equals(texture.ObjectName, map.TextureName, StringComparison.Ordinal)
                || !string.Equals(texture.Bundle, map.BundleId, StringComparison.Ordinal)) continue;
            seen.Add(slot);
        }
        return seen;
    }

    /// <summary>Whether the replacement's donor rows already reference this texture's workspace file, in
    /// which case the edit ships with the Replace and there is nothing to adopt.</summary>
    public static bool ReplacementCarries(ProjectTarget mesh, ProjectTarget texture) =>
        mesh.DonorTextures is { Count: > 0 } rows && rows.Any(r =>
            SameFile(r.Albedo, texture.ReplaceFile)
            || SameFile(r.Normal, texture.ReplaceFile)
            || SameFile(r.Rmo, texture.ReplaceFile));

    /// <summary>What the first landing submesh's slot already holds, in the vocabulary the map cards use.
    /// Asked only where no landing submesh is free, so a holder always answers. The generated donor file
    /// name never appears: it is the build's naming convention, not something the modder chose or can
    /// recognise — what they DID is drop a map on the card, or blank the slot on purpose.</summary>
    public static string Holder(ProjectTarget mesh, DonorMapSlot slot, IReadOnlyList<int> inRange)
    {
        foreach (int i in inRange)
        {
            if (mesh.DonorTextures?.FirstOrDefault(r => r.Submesh == i) is not { } row) continue;
            var (ask, file) = slot switch
            {
                DonorMapSlot.BaseColor => (row.AlbedoAsk, row.Albedo),
                DonorMapSlot.Normal => (row.NormalAsk, row.Normal),
                _ => (row.RmoAsk, row.Rmo),
            };
            if (ask == SlotOrigin.Authored && file is not null)
                return $"already carries its own {RoleLabel(slot)} map";
            if (ask == SlotOrigin.ExplicitNeutral) return "blanks that slot on purpose";
        }
        return "already answers for that slot";
    }

    /// <summary>Take every adoption onto its replacement's donor rows, and report the ones taken — a
    /// non-empty answer is the caller's cue to persist. Mutates the project only — no disk.
    /// <para>Every candidate is re-asked against the project as it stands NOW, since one held back while a
    /// build ran can be answered away before it lands: a Blender send-back or a part Revert can take the
    /// replacement away, another subject's selection can take its claim, shrink the submeshes a row may
    /// name, or fill a slot the modder now owns. An adoption with nothing left to land on is dropped, and
    /// the build's own warning says what became of the edit.</para></summary>
    public static IReadOnlyList<TextureAdoption> Apply(ModProject project,
        IReadOnlyList<TextureAdoption> pending)
    {
        var taken = new List<TextureAdoption>();
        var claims = ReplaceClaims.Of(project);
        foreach (var a in pending)
        {
            if (!project.IsEdited(a.Mesh) || !claims.Ships(a.Mesh)) continue;
            int donorSubmeshes = a.Mesh.DonorMaterials?.Count ?? 0;
            var landing = a.Submeshes
                .Where(i => i >= 0 && i < donorSubmeshes
                    && SlotIsFree(a.Mesh, i, a.Slot, a.Texture.ReplaceFile))
                .ToList();
            if (landing.Count == 0) continue;
            Take(a, landing);
            taken.Add(a with { Submeshes = landing });
        }
        return taken;
    }

    /// <summary>Whether one donor submesh's slot is open to an adoption. A slot carrying an ask of its own —
    /// an authored map, or a deliberate blank — is already the modder's answer for that slot and is never
    /// written over; a slot naming this very file is the adoption itself, already taken. A submesh with no
    /// row yet is open on every slot.</summary>
    public static bool SlotIsFree(ProjectTarget mesh, int submesh, DonorMapSlot slot, string? file)
    {
        if (mesh.DonorTextures?.FirstOrDefault(r => r.Submesh == submesh) is not { } row) return true;
        var (ask, named) = slot switch
        {
            DonorMapSlot.BaseColor => (row.AlbedoAsk, row.Albedo),
            DonorMapSlot.Normal => (row.NormalAsk, row.Normal),
            _ => (row.RmoAsk, row.Rmo),
        };
        return !ask.IsAsk() || SameFile(named, file);
    }

    /// <summary>Whether two workspace paths name one file. Both are project-relative as recorded, so this is
    /// a separator- and case-insensitive text compare, not a disk read.</summary>
    public static bool SameFile(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any donor row of these mesh targets ships this texture's workspace file — i.e. the
    /// texture's edit was adopted and still rides a replacement. What the revert confirm asks before it
    /// promises what a revert does.</summary>
    public static bool CarriesAdoption(IEnumerable<ProjectTarget> meshTargets, ProjectTarget texture) =>
        meshTargets.Any(m => m.DonorTextures?.Any(r =>
            (r.AlbedoAsk == SlotOrigin.Authored && SameFile(r.Albedo, texture.ReplaceFile))
            || (r.NormalAsk == SlotOrigin.Authored && SameFile(r.Normal, texture.ReplaceFile))
            || (r.RmoAsk == SlotOrigin.Authored && SameFile(r.Rmo, texture.ReplaceFile))) == true);

    /// <summary>Take an adoption BACK: reverting the texture is the modder declining it, so every donor slot
    /// shipping this texture's workspace file returns to the part's own stock map. Rows left asking for
    /// nothing are dropped, and a record with no rows left is null, as an untouched replacement's is. A slot
    /// authored from any OTHER file (a drop, a Blender session) is not this texture's to take back and is
    /// left alone.
    /// <para>Answers HOW MANY slots returned: above zero is the caller's cue to persist, and the count is
    /// what lets the status line say "the stock map" or "the stock maps" about what actually happened.</para>
    /// </summary>
    public static int Unadopt(IEnumerable<ProjectTarget> meshTargets, ProjectTarget texture)
    {
        int returned = 0;
        foreach (var mesh in meshTargets)
        {
            if (mesh.DonorTextures is not { } rows) continue;
            int here = 0;
            foreach (var row in rows)
            {
                if (row.AlbedoAsk == SlotOrigin.Authored && SameFile(row.Albedo, texture.ReplaceFile))
                { row.Albedo = null; row.AlbedoOrigin = SlotOrigin.VanillaOwn; here++; }
                if (row.NormalAsk == SlotOrigin.Authored && SameFile(row.Normal, texture.ReplaceFile))
                { row.Normal = null; row.NormalOrigin = SlotOrigin.VanillaOwn; here++; }
                if (row.RmoAsk == SlotOrigin.Authored && SameFile(row.Rmo, texture.ReplaceFile))
                { row.Rmo = null; row.RmoOrigin = SlotOrigin.VanillaOwn; here++; }
            }
            if (here == 0) continue;
            returned += here;
            rows.RemoveAll(r => !r.AlbedoAsk.IsAsk() && !r.NormalAsk.IsAsk() && !r.RmoAsk.IsAsk());
            if (rows.Count == 0) mesh.DonorTextures = null;
        }
        return returned;
    }

    /// <summary>How many adopted maps the line names one by one before it falls back to counting them. Small
    /// on purpose: the point of naming them is that the modder can check the list at a glance.</summary>
    private const int NamedAdoptions = 3;

    /// <summary>What ② Edit says when an edit is adopted: which part took it on which map role, and that the
    /// edit now ships as the replacement's own map. One line for the whole gesture — a map dressing several
    /// parts or several slots is one thing the modder did — and it LOCATES what it announces up to
    /// <see cref="NamedAdoptions"/>, past which the count is the honest summary.</summary>
    public static string Adopted(IReadOnlyList<TextureAdoption> taken)
    {
        if (taken.Count == 1)
            return $"Adopted as {taken[0].Part}'s replacement {RoleLabel(taken[0].Slot)} map.";
        if (taken.Count > NamedAdoptions) return $"Adopted as {taken.Count} replacement maps.";
        var named = taken.Select(a => $"{a.Part}'s {RoleLabel(a.Slot)}").ToList();
        return $"Adopted as {string.Join(", ", named.Take(named.Count - 1))} and {named[^1]} maps.";
    }

    /// <summary>What ② Edit says when the slots an edit would take are already the modder's answers: the
    /// edit stays on the game texture, which the replacement's own draws no longer show. Each held slot
    /// names its holder ONCE — two parts holding a slot the same way is one sentence — and the remedy that
    /// answers all of them closes the line once.</summary>
    public static string SlotTaken(IReadOnlyList<AdoptionBlocked> blocked)
    {
        var clauses = blocked
            .Select(b => $"{b.Part}'s replacement {b.Holder}, so this edit won't show.")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (clauses.Count == 0) return "";
        return string.Join(" ", clauses)
            + " Drop the edited image on the part's map card to use it instead.";
    }

    private static string RoleLabel(DonorMapSlot slot) => slot switch
    {
        DonorMapSlot.BaseColor => TextureMap.BaseColorLabel,
        DonorMapSlot.Normal => TextureMap.NormalLabel,
        _ => TextureMap.RmoLabel,
    };

    private static void Take(TextureAdoption adoption, IReadOnlyList<int> submeshes)
    {
        var rows = adoption.Mesh.DonorTextures ??= new List<SubmeshTextures>();
        foreach (int submesh in submeshes)
        {
            var row = rows.FirstOrDefault(r => r.Submesh == submesh);
            // A submesh with no row yet has every slot still on the part's own stock maps — say so, or the
            // two untouched slots read as "no image at all" and ship the build's flat normal and RMO. An
            // EXISTING row keeps whatever it recorded.
            if (row is null)
                rows.Add(row = new SubmeshTextures
                {
                    Submesh = submesh,
                    AlbedoOrigin = SlotOrigin.VanillaOwn,
                    NormalOrigin = SlotOrigin.VanillaOwn,
                    RmoOrigin = SlotOrigin.VanillaOwn,
                });
            var file = adoption.Texture.ReplaceFile;
            switch (adoption.Slot)
            {
                case DonorMapSlot.BaseColor: row.Albedo = file; row.AlbedoOrigin = SlotOrigin.Authored; break;
                case DonorMapSlot.Normal: row.Normal = file; row.NormalOrigin = SlotOrigin.Authored; break;
                default: row.Rmo = file; row.RmoOrigin = SlotOrigin.Authored; break;
            }
        }
        rows.Sort((a, b) => a.Submesh.CompareTo(b.Submesh));
    }
}
