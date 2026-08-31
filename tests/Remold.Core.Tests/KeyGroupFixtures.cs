using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;

namespace Remold.Core.Tests;

internal static class KeyGroupFixtures
{
    internal static KeyGroup Keyed(this AuthoredProject project, TargetPart target, string key,
        bool startsOff = false, CompositionState offState = CompositionState.Vanilla)
    {
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        string editId = project.Always.First(id => edits[id].Kind == EditDefinitionKind.Content
            && edits[id].Target.SameAs(target));
        project.Always.Remove(editId);
        var active = new List<string> { editId };
        var off = new List<string>();
        if (offState == CompositionState.Hidden) off.Add(Hide(project, target));
        if (startsOff) (active, off) = (off, active);
        var group = new KeyGroup
        {
            Id = $"key-{project.KeyGroups.Count + 1:D4}",
            Key = key,
            States = new List<KeyGroupState>
            {
                new() { Id = "state-0001", ActiveEditIds = active },
                new() { Id = "state-0002", ActiveEditIds = off },
            },
        };
        project.KeyGroups.Add(group);
        return group;
    }

    internal static KeyGroup KeyFirstPart(this AuthoredProject project, string key, bool startsOff = false,
        CompositionState offState = CompositionState.Vanilla)
    {
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        var target = edits[project.Always.First(id => edits[id].Kind == EditDefinitionKind.Content)].Target;
        return project.Keyed(target, key, startsOff, offState);
    }

    internal static string Hide(this AuthoredProject project, TargetPart target)
    {
        var existing = project.EditDefinitions.FirstOrDefault(edit => edit.Kind == EditDefinitionKind.Hide
            && edit.Target.SameAs(target));
        if (existing is not null) return existing.Id;
        var source = project.TargetSlots.First(slot => slot.Targets(target));
        string editId = $"edit-hide-{project.EditDefinitions.Count + 1:D4}";
        string slotId = $"slot-hide-{project.TargetSlots.Count + 1:D4}";
        project.TargetSlots.Add(new TargetSlot
        {
            Id = slotId, Part = target, Tier = source.Tier, Input = TargetInputKind.Visibility,
            Domain = TargetSlotDomain.Game, Renderer = source.Renderer, Mesh = source.Mesh,
        });
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = editId, Kind = EditDefinitionKind.Hide, Target = target, Label = "Hidden",
            Bindings = { new Binding { SlotId = slotId, Kind = BindingKind.Hidden } },
        });
        return editId;
    }

    private static bool Targets(this TargetSlot slot, TargetPart target) => slot.Part.SameAs(target);
}
