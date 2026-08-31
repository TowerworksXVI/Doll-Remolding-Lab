using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>The vocabulary of the derived one-part compatibility account. Vanilla is absence in authored
/// intent; it remains a useful name when projecting that absence into the released workspace.</summary>
public enum CompositionState
{
    Unknown,
    Edit,
    Vanilla,
    Hidden,
}

/// <summary>One part's key answer in the one-switch-per-part vocabulary the released build layers speak.</summary>
public sealed record PartToggle
{
    public string Key { get; init; } = "";
    public bool StartsOff { get; init; }
    public CompositionState OffState { get; init; } = CompositionState.Vanilla;
}

/// <summary>One active edit projected into the released one-change-per-part workspace.</summary>
public sealed record ComposedPart(
    TargetPart Target,
    CompositionState State,
    string? EditDefinitionId,
    bool BuildEnabled,
    PartToggle? Toggle);

/// <summary>Compatibility readings that do not own authored behavior.</summary>
public static class AuthoredComposition
{
    /// <summary>Always-active edits followed by each group's content inventory, retaining the one-content
    /// two-state projection where it applies. A sole unplaced edit is carried as a released unticked row.
    /// The Build plan remains authoritative.</summary>
    public static IReadOnlyList<ComposedPart> Head(AuthoredProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        var result = new List<ComposedPart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var placed = project.Always.Concat(project.KeyGroups.SelectMany(group => group.States)
            .SelectMany(state => state.ActiveEditIds)).ToHashSet(StringComparer.Ordinal);

        foreach (string editId in project.Always ?? new List<string>()) Add(editId, true, null);
        foreach (var group in project.KeyGroups ?? new List<KeyGroup>())
        {
            if (group.States is not { Count: > 0 }) continue;
            var members = group.States.SelectMany(state => state.ActiveEditIds)
                .Where(edits.ContainsKey).Select(id => edits[id])
                .GroupBy(edit => edit.Target.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var memberGroup in members)
            {
                var targetEdits = memberGroup.DistinctBy(edit => edit.Id).ToList();
                var content = targetEdits.Where(edit => edit.Kind == EditDefinitionKind.Content).ToList();
                if (group.Key is { } key && group.States.Count == 2 && content.Count == 1)
                {
                    var positions = group.States.Select((state, index) => (state, index))
                        .Where(item => item.state.ActiveEditIds.Contains(content[0].Id,
                            StringComparer.Ordinal)).Select(item => item.index).ToArray();
                    if (positions.Length == 1)
                    {
                        int active = positions[0];
                        bool hiddenOff = group.States[1 - active].ActiveEditIds.Any(id =>
                            edits.TryGetValue(id, out var candidate)
                            && candidate.Kind == EditDefinitionKind.Hide
                            && candidate.Target.SameAs(content[0].Target));
                        Add(content[0].Id, true, new PartToggle
                        {
                            Key = key,
                            StartsOff = active == 1,
                            OffState = hiddenOff ? CompositionState.Hidden : CompositionState.Vanilla,
                        });
                        continue;
                    }
                }
                foreach (var edit in content) Add(edit.Id, true, null);
                foreach (string editId in group.States[0].ActiveEditIds.Where(id =>
                             edits.TryGetValue(id, out var edit)
                             && edit.Target.SameAs(targetEdits[0].Target)))
                    Add(editId, true, null);
            }
        }
        foreach (var targetEdits in project.EditDefinitions.GroupBy(edit => edit.Target.Key,
                     StringComparer.OrdinalIgnoreCase))
            if (!result.Any(entry => entry.Target.SameAs(targetEdits.First().Target)))
            {
                var unplaced = targetEdits.Where(edit => !placed.Contains(edit.Id)).ToList();
                if (unplaced.Count == 1) Add(unplaced[0].Id, false, null);
            }
        return result;

        void Add(string editId, bool buildEnabled, PartToggle? toggle)
        {
            if (!seen.Add(editId) || !edits.TryGetValue(editId, out var edit)) return;
            result.Add(new ComposedPart(edit.Target,
                edit.Kind == EditDefinitionKind.Hide ? CompositionState.Hidden : CompositionState.Edit,
                edit.Id, buildEnabled, toggle));
        }
    }

    /// <summary>Why a group has no released on/off projection, or null when its first state can be read as
    /// on and its second as off.</summary>
    public static string? UnprojectableReason(KeyGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Key is null) return $"key group '{group.Id}' has no key";
        if (group.States is not { Count: 2 })
            return $"key group '{group.Id}' cycles {group.States?.Count ?? 0} states, and only a two-state "
                + "group has an on/off projection";
        return null;
    }

    public static string MintKeyGroupId(IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.Ordinal);
        return MintKeyGroupId(set, set);
    }

    internal static string MintKeyGroupId(HashSet<string> reserved, HashSet<string> taken)
    {
        for (int n = 1; ; n++)
        {
            string id = $"key-{n:D4}";
            if (taken.Contains(id) || !reserved.Add(id)) continue;
            taken.Add(id);
            return id;
        }
    }
}
