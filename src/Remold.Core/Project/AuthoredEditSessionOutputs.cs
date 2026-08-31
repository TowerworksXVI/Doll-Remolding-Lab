using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Project;

public sealed partial class AuthoredEditSession
{
    /// <summary>Record the submesh layout one content edit's replacement geometry has: the installed
    /// material properties each output submesh folds onto, plus the RMO-alpha answer beside a real RMO.
    /// The installed material inventory is the authority. A replacement never invents a Normal, ramp or
    /// any other input merely because the app knows that kind, and it never learns slots from material names
    /// authored inside the incoming glb.
    ///
    /// <para>This is a layout, not a set of answers: a slot arrives asking the live carrier for its value,
    /// which is what an unauthored submesh has always done — it draws the part's own stock map. The choosers
    /// are how it gets anything else. Slots the layout already holds are left exactly as they are, so a
    /// re-send that returns the same submeshes keeps every answer authored against them.</para>
    ///
    /// <para>A layout that shrinks takes the outputs past its end with it, bindings included: a submesh the
    /// replacement no longer has is a place nothing draws, and a slot left behind would be an answer the
    /// build could never ship. Another edit taking a value from one of them refuses the whole command by
    /// name, the way deleting the edit that owns it does. Project assets those slots named are left alone —
    /// a file no binding uses is still the user's file.</para></summary>
    public void RecordReplacementOutputs(string editDefinitionId, int submeshCount)
    {
        if (submeshCount < 0)
            throw new ArgumentOutOfRangeException(nameof(submeshCount),
                "a replacement cannot have a negative number of submeshes");
        Change(project => RecordReplacementOutputs(project, editDefinitionId, submeshCount));
    }

    private static void RecordReplacementOutputs(AuthoredProject project, string editDefinitionId,
        int submeshCount)
    {
        var edit = RequiredEdit(project, editDefinitionId);
        if (edit.Kind != EditDefinitionKind.Content)
            throw new InvalidOperationException($"'{edit.Label}' hides the part, so it has no "
                + "replacement of its own to record.");
        var anchor = ReplacementAnchor(project, edit);

        var bound = edit.Bindings.Select(binding => binding.SlotId).ToHashSet(StringComparer.Ordinal);
        var owned = project.TargetSlots.Where(slot => slot.Domain == TargetSlotDomain.EditOutput
            && bound.Contains(slot.Id)
            && slot.SubmeshIndex is not null
            && (IsTextureInput(slot.Input) || slot.Input == TargetInputKind.RmoAlpha)).ToList();

        var gameRows = project.TargetSlots.Where(slot => slot.Part.SameAs(edit.Target)
                && slot.Domain == TargetSlotDomain.Game && slot.MaterialSlotIndex is not null
                && IsTextureInput(slot.Input) && slot.MaterialBindingPresent != false)
            .OrderBy(slot => slot.MaterialSlotIndex)
            .ThenBy(slot => slot.Id, StringComparer.Ordinal).ToList();
        // Game-domain rows may be filed once per edit, but they still describe one installed binding. Prefer
        // exact property rows where the project also carries their pre-property schema-2 spelling; retain
        // distinct exact properties of the same coarse kind.
        var game = gameRows.GroupBy(slot => (slot.MaterialSlotIndex, slot.Input))
            .SelectMany(group =>
            {
                var exact = group.Where(slot => !string.IsNullOrWhiteSpace(slot.ShaderProperty))
                    .GroupBy(slot => slot.ShaderProperty!, StringComparer.Ordinal)
                    .Select(properties => properties.First()).ToList();
                return exact.Count > 0 ? exact : group.Take(1);
            })
            .OrderBy(slot => slot.MaterialSlotIndex)
            .ThenBy(slot => slot.Id, StringComparer.Ordinal).ToList();
        int lastKnownPosition = game.Count == 0 ? -1 : game.Max(slot => slot.MaterialSlotIndex!.Value);
        int MaterialPosition(int submesh)
        {
            if (anchor.MaterialIndexCounts is not { Count: > 0 } counts)
                return lastKnownPosition < 0 ? -1 : Math.Min(submesh, lastKnownPosition);
            int lastDrawable = -1;
            for (int position = 0; position < counts.Count; position++)
                if (counts[position] > 0) lastDrawable = position;
            if (lastDrawable < 0) return -1;
            int folded = Math.Min(submesh, counts.Count - 1);
            return counts[folded] == 0 ? lastDrawable : folded;
        }

        var desired = new List<(int Submesh, int MaterialPosition, TargetInputKind Input,
            string? ShaderProperty)>();
        for (int submesh = 0; submesh < submeshCount; submesh++)
        {
            int materialPosition = MaterialPosition(submesh);
            if (materialPosition < 0) continue;
            foreach (var slot in game.Where(slot => slot.MaterialSlotIndex == materialPosition))
            {
                desired.Add((submesh, materialPosition, slot.Input, slot.ShaderProperty));
                if (slot.Input == TargetInputKind.Rmo)
                    desired.Add((submesh, materialPosition, TargetInputKind.RmoAlpha, null));
            }
        }

        // Reuse exact rows first. A property-less known row is the pre-property schema-2 spelling and is
        // enriched rather than discarded, preserving the answer already bound to it. It can satisfy only
        // one installed property; a second property of that coarse kind receives a distinct slot.
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in desired)
        {
            var existing = owned.FirstOrDefault(slot => !kept.Contains(slot.Id)
                && slot.SubmeshIndex == route.Submesh && slot.Input == route.Input
                && string.Equals(slot.ShaderProperty, route.ShaderProperty, StringComparison.Ordinal));
            existing ??= owned.FirstOrDefault(slot => !kept.Contains(slot.Id)
                && slot.SubmeshIndex == route.Submesh && slot.Input == route.Input
                && route.Input != TargetInputKind.Texture
                && string.IsNullOrWhiteSpace(slot.ShaderProperty));
            if (existing is null) continue;
            existing.ShaderProperty = route.ShaderProperty;
            existing.MaterialSlotIndex = route.MaterialPosition;
            kept.Add(existing.Id);
        }

        var dropped = owned.Where(slot => !kept.Contains(slot.Id))
            .Select(slot => slot.Id).ToHashSet(StringComparer.Ordinal);
        if (dropped.Count > 0)
        {
            var borrower = project.EditDefinitions.FirstOrDefault(other =>
                !string.Equals(other.Id, edit.Id, StringComparison.Ordinal)
                && other.Bindings.Any(binding => binding.SourceSlot is { } from
                    && dropped.Contains(from.SlotId)));
            if (borrower is not null)
                throw new InvalidOperationException($"'{edit.Label}' cannot drop those submeshes while "
                    + $"'{borrower.Label}' takes a value from one of them.");
            project.TargetSlots.RemoveAll(slot => dropped.Contains(slot.Id));
            edit.Bindings.RemoveAll(binding => dropped.Contains(binding.SlotId));
        }

        var taken = project.TargetSlots.Select(slot => slot.Id).ToList();
        foreach (var route in desired)
        {
            if (kept.Any(id => project.TargetSlots.Any(slot => slot.Id == id
                    && slot.SubmeshIndex == route.Submesh && slot.Input == route.Input
                    && string.Equals(slot.ShaderProperty, route.ShaderProperty,
                        StringComparison.Ordinal))))
                continue;
            string id = MintId("slot", taken);
            taken.Add(id);
            project.TargetSlots.Add(new TargetSlot
            {
                Id = id,
                Part = Clone(edit.Target),
                SubmeshIndex = route.Submesh,
                MaterialSlotIndex = route.MaterialPosition,
                Input = route.Input,
                ShaderProperty = route.ShaderProperty,
                Domain = TargetSlotDomain.EditOutput,
                Renderer = Clone(anchor.Renderer),
                Mesh = anchor.Mesh is null ? null : Clone(anchor.Mesh),
            });
            edit.Bindings.Add(new Binding
            {
                SlotId = id,
                Kind = BindingKind.InheritedLiveCarrier,
            });
        }
    }

    /// <summary>The game object a replacement's outputs are recorded against: the part's own lod0 geometry
    /// slot, which is the renderer and mesh the adapter reads a donor row's identity off. A part whose slots
    /// the project has never opened has nothing to anchor on, and that is said out loud rather than minting
    /// outputs that address nothing.</summary>
    private static TargetSlot ReplacementAnchor(AuthoredProject project, EditDefinition edit) =>
        project.TargetSlots
            .Where(slot => slot.Part.SameAs(edit.Target) && slot.Domain == TargetSlotDomain.Game
                && slot.Input == TargetInputKind.Geometry
                && (slot.Tier is null || string.Equals(slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(slot => slot.Id, StringComparer.Ordinal)
            .FirstOrDefault()
        ?? throw new InvalidOperationException(
            $"{AuthoredBuildPlanner.PartName(edit.Target)} has no mesh in this mod to record a "
            + "replacement against.");
}
