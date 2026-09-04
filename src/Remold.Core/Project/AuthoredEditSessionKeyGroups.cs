using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>Edit activation and key-group verbs. Every command commits one complete, validated shape.</summary>
public sealed partial class AuthoredEditSession
{
    /// <summary>Why a key group refuses to lose a state, in the words the Build page also greys its remove
    /// button with. One sentence, one home: the page states the rule before the click and the command
    /// states it after, and the two cannot drift apart.</summary>
    public const string TwoStateFloor =
        "A key needs two states to switch between. Delete the group instead.";

    /// <summary>Create a two-state group with the picked edit active in the first state. An Always placement
    /// of that edit moves into the group so the new key can affect it.</summary>
    public string CreateKeyGroup(string? key, string editDefinitionId, string? label = null)
    {
        string id = "";
        Change(project =>
        {
            var edit = RequiredEdit(project, editDefinitionId);
            string? normalized = NormalizeOptionalKey(key);
            if (normalized is not null) RefuseSharedKey(project, normalized, null);
            id = AuthoredComposition.MintKeyGroupId(project.KeyGroups.Select(group => group.Id));
            project.Always.RemoveAll(candidate => string.Equals(candidate, edit.Id, StringComparison.Ordinal));
            project.KeyGroups.Add(new KeyGroup
            {
                Id = id,
                Key = normalized,
                Label = Trimmed(label),
                States =
                {
                    new KeyGroupState { Id = "state-0001", ActiveEditIds = { edit.Id } },
                    new KeyGroupState { Id = "state-0002" },
                },
            });
        });
        return id;
    }

    /// <summary>Delete a group and every placement and state it owns. Edits remain in the library.</summary>
    public void DeleteKeyGroup(string keyGroupId) => Change(project =>
        project.KeyGroups.Remove(RequiredGroup(project, keyGroupId)));

    /// <summary>Assign another key, or clear it with null or whitespace.</summary>
    public void SetGroupKey(string keyGroupId, string? key) => Change(project =>
    {
        var group = RequiredGroup(project, keyGroupId);
        string? normalized = NormalizeOptionalKey(key);
        if (normalized is not null) RefuseSharedKey(project, normalized, group.Id);
        group.Key = normalized;
    });

    public void RenameGroup(string keyGroupId, string? label) =>
        Change(project => RequiredGroup(project, keyGroupId).Label = Trimmed(label));

    /// <summary>Choose whether the group's position survives a game restart. False is the per-session
    /// reset every group starts with.</summary>
    public void SetGroupPersistence(string keyGroupId, bool persist) =>
        Change(project => RequiredGroup(project, keyGroupId).Persist = persist);

    public void RenameState(string keyGroupId, string stateId, string? label) => Change(project =>
        RequiredState(RequiredGroup(project, keyGroupId), stateId).Label = Trimmed(label));

    /// <summary>Place an edit in Always. Every edit takes this one route, a hide included: a hide edit is an
    /// ordinary edit, so it is minted by its own command and placed by this one.</summary>
    public void PlaceEdit(string editDefinitionId) => Change(project =>
        AddPlacement(project.Always, RequiredEdit(project, editDefinitionId), PlacementNames.Always));

    /// <summary>Place an edit in one stable state.</summary>
    public void PlaceEdit(string editDefinitionId, string keyGroupId, string stateId) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var group = RequiredGroup(project, keyGroupId);
        var state = RequiredState(group, stateId);
        AddPlacement(state.ActiveEditIds, edit, PlacementNames.Place(group, state));
    });

    public void PlaceEdit(string editDefinitionId, string keyGroupId, int stateIndex) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var group = RequiredGroup(project, keyGroupId);
        var state = RequiredState(group, stateIndex);
        AddPlacement(state.ActiveEditIds, edit, PlacementNames.Place(group, state));
    });

    public void UnplaceEdit(string editDefinitionId) => Change(project =>
        RemovePlacement(project.Always, RequiredEdit(project, editDefinitionId), PlacementNames.Always));

    public void UnplaceEdit(string editDefinitionId, string keyGroupId, string stateId) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var group = RequiredGroup(project, keyGroupId);
        var state = RequiredState(group, stateId);
        RemovePlacement(state.ActiveEditIds, edit, PlacementNames.Place(group, state));
    });

    public void UnplaceEdit(string editDefinitionId, string keyGroupId, int stateIndex) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var group = RequiredGroup(project, keyGroupId);
        var state = RequiredState(group, stateIndex);
        RemovePlacement(state.ActiveEditIds, edit, PlacementNames.Place(group, state));
    });

    /// <summary>Place an edit where a part is answered exactly once, taking that answer's seat. A part has
    /// one content edit in any one place, so seating a second takes the seat from the first rather than
    /// stacking on it — and the eviction and the placement are one transaction, so a placement the model
    /// refuses cannot leave the incumbent unseated. A hide takes no seat: a part has one, by construction.
    ///
    /// <para>Null group and state ids name Always, which answers for a part exactly as a state does.</para>
    /// </summary>
    public void SeatEdit(string editDefinitionId, string? keyGroupId = null, string? stateId = null) =>
        Change(project =>
        {
            var edit = RequiredEdit(project, editDefinitionId);
            var to = PlacementList(project, keyGroupId, stateId);
            Seat(project, edit, to);
        });

    /// <summary>Move one placement, taking the destination's content seat as a placement takes it. Null
    /// group and state ids name Always. One transaction: a move the model refuses leaves both the source
    /// placement and whatever sits at the destination exactly as they were.</summary>
    public void MovePlacement(string editDefinitionId, string? fromGroupId, string? fromStateId,
        string? toGroupId, string? toStateId) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var from = PlacementList(project, fromGroupId, fromStateId);
        var to = PlacementList(project, toGroupId, toStateId);
        if (ReferenceEquals(from.List, to.List)) return;
        RemovePlacement(from.List, edit, from.Name);
        Seat(project, edit, to);
    });

    /// <summary>Clear the destination's content answer for this edit's part, then place it there.</summary>
    private static void Seat(AuthoredProject project, EditDefinition edit,
        (List<string> List, string Name) to)
    {
        if (edit.Kind == EditDefinitionKind.Content)
            to.List.RemoveAll(id => !string.Equals(id, edit.Id, StringComparison.Ordinal)
                && project.EditDefinitions.Any(seated =>
                    string.Equals(seated.Id, id, StringComparison.Ordinal)
                    && seated.Kind == EditDefinitionKind.Content
                    && seated.Target.SameAs(edit.Target)));
        AddPlacement(to.List, edit, to.Name);
    }

    /// <summary>The edit a placement of <paramref name="editDefinitionId"/> at one place would unseat, or
    /// null. Read before the write by a surface that has to say what an action took away.</summary>
    public string? SeatHolder(string editDefinitionId, string? keyGroupId = null, string? stateId = null)
    {
        lock (_gate)
        {
            var edit = RequiredEdit(_project, editDefinitionId);
            if (edit.Kind != EditDefinitionKind.Content) return null;
            var seated = PlacementList(_project, keyGroupId, stateId).List;
            return _project.EditDefinitions.FirstOrDefault(candidate =>
                candidate.Kind == EditDefinitionKind.Content
                && !string.Equals(candidate.Id, edit.Id, StringComparison.Ordinal)
                && candidate.Target.SameAs(edit.Target)
                && seated.Contains(candidate.Id, StringComparer.Ordinal))?.Id;
        }
    }

    /// <summary>Duplicate one state, including all placements, and return the new stable id.</summary>
    public string DuplicateState(string keyGroupId, string stateId, string? label = null)
    {
        string id = "";
        Change(project =>
        {
            var group = RequiredGroup(project, keyGroupId);
            var source = RequiredState(group, stateId);
            id = MintStateId(group);
            group.States.Add(new KeyGroupState
            {
                Id = id,
                Label = label is null ? source.Label : Trimmed(label),
                ActiveEditIds = source.ActiveEditIds.ToList(),
            });
        });
        return id;
    }

    public string DuplicateState(string keyGroupId, int stateIndex, string? label = null)
    {
        string id = "";
        Change(project =>
        {
            var group = RequiredGroup(project, keyGroupId);
            var source = RequiredState(group, stateIndex);
            id = MintStateId(group);
            group.States.Add(new KeyGroupState
            {
                Id = id,
                Label = label is null ? source.Label : Trimmed(label),
                ActiveEditIds = source.ActiveEditIds.ToList(),
            });
        });
        return id;
    }

    public void RemoveState(string keyGroupId, string stateId) => Change(project =>
    {
        var group = RequiredGroup(project, keyGroupId);
        if (group.States.Count <= 2)
            throw new AuthoredRefusalException(TwoStateFloor);
        group.States.Remove(RequiredState(group, stateId));
    });

    public void RemoveState(string keyGroupId, int stateIndex) => Change(project =>
    {
        var group = RequiredGroup(project, keyGroupId);
        if (group.States.Count <= 2)
            throw new AuthoredRefusalException(TwoStateFloor);
        group.States.RemoveAt(RequiredStateIndex(group, stateIndex));
    });

    public void ReorderState(string keyGroupId, int fromIndex, int toIndex) => Change(project =>
    {
        var group = RequiredGroup(project, keyGroupId);
        int from = RequiredStateIndex(group, fromIndex);
        int to = RequiredStateIndex(group, toIndex);
        var moved = group.States[from];
        group.States.RemoveAt(from);
        group.States.Insert(to, moved);
    });

    /// <summary>Remove one whole subject and every placement of its edits. Key groups remain authored.</summary>
    public void ForgetSubject(string subject, string outfit) =>
        Change(project => ForgetSubject(project, subject, outfit));

    private static void ForgetSubject(AuthoredProject project, string subject, string outfit)
    {
        bool Owned(TargetPart? part) => part is not null
            && string.Equals(part.Subject, subject, StringComparison.OrdinalIgnoreCase)
            && string.Equals(part.Outfit, outfit, StringComparison.OrdinalIgnoreCase);

        var edits = project.EditDefinitions.Where(edit => Owned(edit.Target))
            .Select(edit => edit.Id).ToHashSet(StringComparer.Ordinal);
        project.Always.RemoveAll(edits.Contains);
        foreach (var state in project.KeyGroups.SelectMany(group => group.States))
            state.ActiveEditIds.RemoveAll(edits.Contains);
        project.EditDefinitions.RemoveAll(edit => edits.Contains(edit.Id));

        var gone = project.TargetSlots.Where(slot => Owned(slot.Part))
            .Select(slot => slot.Id).ToHashSet(StringComparer.Ordinal);
        project.TargetSlots.RemoveAll(slot => gone.Contains(slot.Id));
        project.WorkspaceIndex?.Records.RemoveAll(record => Owned(record.Part));
        project.WorkspaceIndex?.Selection.RemoveAll(selection =>
            string.Equals(selection.Character, subject, StringComparison.OrdinalIgnoreCase)
            && string.Equals(selection.Outfit, outfit, StringComparison.OrdinalIgnoreCase));

        foreach (var edit in project.EditDefinitions)
        {
            edit.Bindings.RemoveAll(binding => gone.Contains(binding.SlotId));
            foreach (var binding in edit.Bindings.Where(binding =>
                         binding.SourceSlot is { } from && gone.Contains(from.SlotId)).ToList())
            {
                binding.Kind = BindingKind.TargetGameValue;
                binding.SourceSlot = null;
                binding.ProjectAssetId = null;
            }
        }
    }

    /// <summary>A detached edit-first reading: every edit with all placements, followed by the Always list
    /// and each group with its stable states.</summary>
    public AuthoredEditOutline Outline()
    {
        lock (_gate)
        {
            var placements = _project.EditDefinitions.ToDictionary(edit => edit.Id,
                _ => new List<EditPlacementOutline>(), StringComparer.Ordinal);
            foreach (string editId in _project.Always)
                if (placements.TryGetValue(editId, out var list)) list.Add(EditPlacementOutline.Always);
            foreach (var group in _project.KeyGroups)
                for (int i = 0; i < group.States.Count; i++)
                    foreach (string editId in group.States[i].ActiveEditIds)
                        if (placements.TryGetValue(editId, out var list))
                            list.Add(new EditPlacementOutline(group.Id, group.States[i].Id, i));

            var edits = _project.EditDefinitions.Select(edit => new AuthoredEditOutlineEntry(edit.Id,
                edit.Kind, Clone(edit.Target), edit.Label, placements[edit.Id].ToArray(),
                edit.ReturnWarning)).ToArray();
            var groups = _project.KeyGroups.Select(group => new KeyGroupOutline(group.Id, group.Key,
                group.Label, group.States.Select(state => new KeyGroupStateOutline(state.Id, state.Label,
                    state.ActiveEditIds.ToArray())).ToArray(), group.Persist)).ToArray();
            var knownParts = _project.TargetSlots.Select(slot => slot.Part)
                .Concat(_project.EditDefinitions.Select(edit => edit.Target))
                .Concat(_project.WorkspaceIndex?.Records.Select(record => record.Part)
                    ?? Enumerable.Empty<TargetPart>())
                .GroupBy(target => $"{target.Subject}\u001f{target.Outfit}\u001f{target.RendererSlot}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => Clone(group.First())).ToArray();
            return new AuthoredEditOutline(edits, _project.Always.ToArray(), groups, knownParts);
        }
    }

    private static void AddPlacement(List<string> placements, EditDefinition edit, string at)
    {
        if (placements.Contains(edit.Id, StringComparer.Ordinal))
            throw new AuthoredRefusalException($"{EditName(edit)} is already used in {at}.");
        placements.Add(edit.Id);
    }

    private static void RemovePlacement(List<string> placements, EditDefinition edit, string at)
    {
        if (placements.RemoveAll(id => string.Equals(id, edit.Id, StringComparison.Ordinal)) == 0)
            throw new AuthoredRefusalException($"{EditName(edit)} isn't used in {at}.");
    }

    /// <summary>What a refusal calls an edit: the name the modder gave it, and the part's name behind an
    /// edit that has none.</summary>
    private static string EditName(EditDefinition edit) =>
        !string.IsNullOrWhiteSpace(edit.Label) ? edit.Label.Trim()
        : $"the edit on {edit.Target.RendererSlot}";

    private static (List<string> List, string Name) PlacementList(AuthoredProject project,
        string? groupId, string? stateId)
    {
        if (groupId is null && stateId is null) return (project.Always, PlacementNames.Always);
        if (groupId is null || stateId is null)
            throw new InvalidOperationException("a state placement needs both a key-group id and state id");
        var group = RequiredGroup(project, groupId);
        var state = RequiredState(group, stateId);
        return (state.ActiveEditIds, PlacementNames.Place(group, state));
    }

    private static KeyGroup RequiredGroup(AuthoredProject project, string id) =>
        project.KeyGroups.SingleOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"key group '{id}' does not exist");

    private static KeyGroupState RequiredState(KeyGroup group, string id) =>
        group.States.SingleOrDefault(state => string.Equals(state.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"key group '{group.Id}' has no state '{id}'");

    private static KeyGroupState RequiredState(KeyGroup group, int index) =>
        group.States[RequiredStateIndex(group, index)];

    private static int RequiredStateIndex(KeyGroup group, int index) =>
        index >= 0 && index < group.States.Count ? index
        : throw new ArgumentOutOfRangeException(nameof(index), index,
            $"key group '{group.Id}' has {group.States.Count} states");

    private static string MintStateId(KeyGroup group)
    {
        int highest = 0;
        foreach (string id in group.States.Select(state => state.Id))
            if (id.StartsWith("state-", StringComparison.Ordinal)
                && int.TryParse(id.AsSpan("state-".Length), out int value))
                highest = Math.Max(highest, value);
        return $"state-{checked(highest + 1):D4}";
    }

    private static string? NormalizeOptionalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return ModKeys.Normalize(key)
            ?? throw new AuthoredRefusalException($"{key} cannot be used as a toggle key.");
    }

    private static void RefuseSharedKey(AuthoredProject project, string key, string? exceptGroupId)
    {
        var holder = project.KeyGroups.FirstOrDefault(group =>
            !string.Equals(group.Id, exceptGroupId, StringComparison.Ordinal)
            && group.Key is not null && ModKeys.SameKey(group.Key, key));
        // Named by its label rather than by GroupName: the holder holds THIS key, so naming it by the key
        // would answer the question with the question.
        if (holder is not null)
            throw new AuthoredRefusalException($"Key {key} is already used by "
                + (!string.IsNullOrWhiteSpace(holder.Label) ? holder.Label.Trim() : "another key group")
                + ".");
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AuthoredEditOutline(IReadOnlyList<AuthoredEditOutlineEntry> Edits,
    IReadOnlyList<string> Always, IReadOnlyList<KeyGroupOutline> Groups,
    IReadOnlyList<TargetPart> KnownParts);

public sealed record AuthoredEditOutlineEntry(string Id, EditDefinitionKind Kind, TargetPart Target,
    string Label, IReadOnlyList<EditPlacementOutline> Placements, string? ReturnWarning = null);

public sealed record EditPlacementOutline(string? KeyGroupId, string? StateId, int? StateIndex)
{
    public static EditPlacementOutline Always { get; } = new(null, null, null);
    public bool IsAlways => KeyGroupId is null;
}

public sealed record KeyGroupOutline(string Id, string? Key, string? Label,
    IReadOnlyList<KeyGroupStateOutline> States, bool Persist = false);

public sealed record KeyGroupStateOutline(string Id, string? Label, IReadOnlyList<string> ActiveEditIds);
