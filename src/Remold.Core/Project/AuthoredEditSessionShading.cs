using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>The material a shading copy came from, as the edit records it.</summary>
public sealed record MaterialShadingSource(TargetPart Part, int MaterialSlotIndex, string MaterialName);

public sealed partial class AuthoredEditSession
{
    /// <summary>Commit a shading answer, including any ramp selection, as one transaction. Off
    /// switches never replace authored numeric values with their neutral build-time inputs. A copy
    /// records where it came from; that record lasts as long as anything copied remains at the
    /// position.</summary>
    public void ApplyMaterialShading(string editDefinitionId, TargetPart target, int materialSlotIndex,
        IReadOnlyList<AuthoredMaterialValueEdit> edits,
        IReadOnlyList<AuthoredMaterialEffectEdit> effectEdits,
        Func<TargetPart, LegacyResolvedPart?> resolvePart,
        IReadOnlyList<MaterialEffectOperation>? effectOperations = null,
        TargetPart? sourceRampPart = null, int sourceRampMaterialSlotIndex = 0,
        MaterialShadingSource? copiedFrom = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(effectEdits);
        ArgumentNullException.ThrowIfNull(resolvePart);
        ArgumentOutOfRangeException.ThrowIfNegative(materialSlotIndex);
        var prepared = edits.Select(edit =>
        {
            var field = MaterialValueCatalog.Field(edit.Semantic)
                ?? throw new ArgumentException($"'{edit.Semantic}' is not an authorable shading value",
                    nameof(edits));
            if (string.IsNullOrWhiteSpace(edit.Value)) return (field, Value: (string?)null);
            if (!MaterialValueBuildSupport.TryValues(edit.Semantic, edit.Value, out _,
                    out string canonical))
                throw MaterialValueArgument(field, edit.Value);
            return (field, Value: canonical);
        }).ToList();
        if (prepared.Select(item => item.field.Semantic).Distinct(StringComparer.Ordinal).Count()
            != prepared.Count)
            throw new ArgumentException("a shading field was supplied more than once", nameof(edits));
        var effectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in effectEdits)
        {
            var definition = MaterialEffectCatalog.Definition(effect.EffectId)
                ?? throw new ArgumentException($"Unknown material effect '{effect.EffectId}'.",
                    nameof(effectEdits));
            if (!effectIds.Add(effect.EffectId))
                throw new ArgumentException("A material effect was supplied more than once.",
                    nameof(effectEdits));
            if (effect.Enabled) continue;
            if (effectOperations?.Any(operation => string.Equals(operation.EffectId, effect.EffectId,
                    StringComparison.Ordinal)) != true)
                throw new AuthoredRefusalException(
                    $"{definition.Label} cannot be disabled on this material.");
        }
        bool needsResolved = prepared.Any(item => item.Value is not null)
            || effectEdits.Any(effect => !effect.Enabled) || sourceRampPart is not null;
        var resolved = needsResolved ? resolvePart(Clone(target)) : null;
        if (needsResolved && resolved is null)
            throw new AuthoredRefusalException(PartNotInstalled);
        LegacyResolvedPart? sourceResolved = null;
        if (sourceRampPart is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sourceRampMaterialSlotIndex);
            sourceResolved = resolvePart(Clone(sourceRampPart))
                ?? throw new AuthoredRefusalException(SourcePartNotInstalled);
        }
        ChangeWithValueFiles((project, writes) =>
        {
            var edit = RequiredEdit(project, editDefinitionId);
            if (edit.Kind != EditDefinitionKind.Content || !edit.Target.SameAs(target))
                throw new AuthoredRefusalException("This shading answer belongs to another edit.");
            if (effectEdits.Any(effect => !effect.Enabled))
            {
                if (!(resolved!.Materials ?? Array.Empty<LegacyResolvedMaterial>())
                    .Any(material => material.MaterialSlotIndex == materialSlotIndex))
                    throw new AuthoredRefusalException($"This part has no material {materialSlotIndex}.");
                EnsurePartSlots(project, target, resolved);
            }
            foreach (var (field, canonical) in prepared)
            {
                var slot = MaterialValueSlot(project, target, materialSlotIndex, field.Semantic);
                if (canonical is null)
                {
                    if (slot is not null)
                        SetBinding(project, editDefinitionId,
                            new Binding { SlotId = slot.Id, Kind = BindingKind.TargetGameValue });
                    continue;
                }
                string slotId = slot?.Id ?? EnsureMaterialValueSlot(project, target,
                    materialSlotIndex, field.Semantic, resolved!);
                SetMaterialValue(project, editDefinitionId, slotId, field, field.Semantic,
                    canonical, writes);
            }
            foreach (var effect in effectEdits)
            {
                edit.DisabledMaterialEffects?.RemoveAll(item =>
                    item.MaterialSlotIndex == materialSlotIndex && item.EffectId == effect.EffectId);
                if (!effect.Enabled)
                    (edit.DisabledMaterialEffects ??= new()).Add(
                        new DisabledMaterialEffect(materialSlotIndex, effect.EffectId));
            }
            if (edit.DisabledMaterialEffects is { Count: 0 }) edit.DisabledMaterialEffects = null;
            if (sourceRampPart is not null)
            {
                EnsurePartSlots(project, target, resolved!);
                EnsurePartSlots(project, sourceRampPart, sourceResolved!);
                string onto = RampSlot(project, target, materialSlotIndex);
                string from = RampSlot(project, sourceRampPart, sourceRampMaterialSlotIndex);
                SetBinding(project, editDefinitionId, string.Equals(onto, from, StringComparison.Ordinal)
                    ? new Binding { SlotId = onto, Kind = BindingKind.TargetGameValue }
                    : new Binding
                    {
                        SlotId = onto,
                        Kind = BindingKind.SourceSlot,
                        SourceSlot = new BindingSourceSlot { SlotId = from },
                    });
            }
            if (copiedFrom is not null)
            {
                edit.CopiedMaterialShading?.RemoveAll(copy => copy.MaterialSlotIndex == materialSlotIndex);
                (edit.CopiedMaterialShading ??= new()).Add(new CopiedMaterialShading(materialSlotIndex,
                    Clone(copiedFrom.Part), copiedFrom.MaterialSlotIndex, copiedFrom.MaterialName));
            }
            RemoveUnauthoredMaterialValueSlots(project);
            if (edit.CopiedMaterialShading is { } copies
                && !PositionHoldsShading(project, edit, target, materialSlotIndex))
                copies.RemoveAll(copy => copy.MaterialSlotIndex == materialSlotIndex);
            if (edit.CopiedMaterialShading is { Count: 0 }) edit.CopiedMaterialShading = null;
        });
    }

    /// <summary>Whether the edit still sets anything at one material position: a value of its own or
    /// copied, an off switch, or a ramp taken from another material.</summary>
    private static bool PositionHoldsShading(AuthoredProject project, EditDefinition edit,
        TargetPart target, int materialSlotIndex)
    {
        if (edit.DisabledMaterialEffects?.Any(effect => effect.MaterialSlotIndex == materialSlotIndex) == true)
            return true;
        var position = project.TargetSlots.Where(slot => slot.Part.SameAs(target)
            && slot.Domain == TargetSlotDomain.Game
            && (slot.MaterialSlotIndex ?? slot.SubmeshIndex) == materialSlotIndex).ToList();
        var values = position.Where(slot => slot.Input == TargetInputKind.MaterialValue)
            .Select(slot => slot.Id).ToHashSet(StringComparer.Ordinal);
        var ramps = position.Where(slot => slot.Input == TargetInputKind.Ramp)
            .Select(slot => slot.Id).ToHashSet(StringComparer.Ordinal);
        return edit.Bindings.Any(binding =>
            values.Contains(binding.SlotId) && binding.Kind is BindingKind.ProjectAsset or BindingKind.SourceSlot
            || ramps.Contains(binding.SlotId) && binding.Kind == BindingKind.SourceSlot);
    }

    private static string RampSlot(AuthoredProject project, TargetPart target, int materialSlotIndex)
    {
        var slots = project.TargetSlots.Where(slot => slot.Part.SameAs(target)
            && slot.Domain == TargetSlotDomain.Game && slot.Input == TargetInputKind.Ramp
            && slot.MaterialSlotIndex == materialSlotIndex).OrderBy(slot => slot.Id, StringComparer.Ordinal)
            .ToList();
        if (slots.Count == 0)
            throw new AuthoredRefusalException(
                $"Material {materialSlotIndex} draws without a toon ramp, so one cannot be copied here.");
        return slots[0].Id;
    }
}
