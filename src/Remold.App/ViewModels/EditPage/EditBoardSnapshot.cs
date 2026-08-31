using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;

namespace Remold.App.ViewModels.EditPage;

/// <summary>A detached, indexed reading used by one Edit-board rebuild. It is built from the session's one
/// project snapshot, so every row in a revision sees the same project and no row repeats the model's global
/// slot, asset, edit, or placement scans.</summary>
internal sealed class EditBoardSnapshot
{
    private static readonly IReadOnlyList<EditSlotState> NoSlots = Array.Empty<EditSlotState>();
    private static readonly IReadOnlyList<AuthoredEditOutlineEntry> NoEdits =
        Array.Empty<AuthoredEditOutlineEntry>();

    private readonly Dictionary<string, IReadOnlyList<EditSlotState>> _slotsByEdit;
    private readonly Dictionary<string, IReadOnlyList<AuthoredEditOutlineEntry>> _editsByPart;
    private readonly Dictionary<string, EditSlotState> _slotsByEditAndId;
    private readonly Dictionary<string, TargetSlot> _slotsById;
    private readonly Dictionary<string, IReadOnlyList<TargetSlot>> _slotsByPart;

    private EditBoardSnapshot(AuthoredProject project,
        IReadOnlyList<AuthoredEditOutlineEntry> edits, IReadOnlyList<TargetPart> knownParts,
        Dictionary<string, IReadOnlyList<EditSlotState>> slotsByEdit,
        Dictionary<string, IReadOnlyList<AuthoredEditOutlineEntry>> editsByPart,
        Dictionary<string, EditSlotState> slotsByEditAndId,
        Dictionary<string, TargetSlot> slotsById,
        Dictionary<string, IReadOnlyList<TargetSlot>> slotsByPart)
    {
        Project = project;
        Edits = edits;
        KnownParts = knownParts;
        _slotsByEdit = slotsByEdit;
        _editsByPart = editsByPart;
        _slotsByEditAndId = slotsByEditAndId;
        _slotsById = slotsById;
        _slotsByPart = slotsByPart;
    }

    public AuthoredProject Project { get; }
    public IReadOnlyList<AuthoredEditOutlineEntry> Edits { get; }
    public IReadOnlyList<TargetPart> KnownParts { get; }

    public IReadOnlyList<EditSlotState> Slots(string editId) =>
        _slotsByEdit.GetValueOrDefault(editId) ?? NoSlots;

    public IReadOnlyList<AuthoredEditOutlineEntry> EditsFor(TargetPart part) =>
        _editsByPart.GetValueOrDefault(PartKey(part)) ?? NoEdits;

    public EditSlotState? Slot(string editId, string slotId) =>
        _slotsByEditAndId.GetValueOrDefault(EditSlotKey(editId, slotId));

    public TargetSlot? Slot(string slotId) => _slotsById.GetValueOrDefault(slotId);

    public IReadOnlyList<TargetSlot> SlotsFor(TargetPart part) =>
        _slotsByPart.GetValueOrDefault(PartKey(part)) ?? Array.Empty<TargetSlot>();

    public string Lod0MeshName(TargetPart part) =>
        SlotsFor(part).FirstOrDefault(slot => slot.Input == TargetInputKind.Geometry
            && string.Equals(slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase))?.Mesh?.Name ?? "";

    public AuthoredEditOutlineEntry? Edit(string editId) =>
        Edits.FirstOrDefault(edit => string.Equals(edit.Id, editId, StringComparison.Ordinal));

    public static EditBoardSnapshot Create(AuthoredProject project)
    {
        var slotsById = project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var assetsById = project.ProjectAssets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);

        var placements = project.EditDefinitions.ToDictionary(edit => edit.Id,
            _ => new List<EditPlacementOutline>(), StringComparer.Ordinal);
        foreach (string editId in project.Always)
            if (placements.TryGetValue(editId, out var list)) list.Add(EditPlacementOutline.Always);
        foreach (var group in project.KeyGroups)
            for (int i = 0; i < group.States.Count; i++)
                foreach (string editId in group.States[i].ActiveEditIds)
                    if (placements.TryGetValue(editId, out var list))
                        list.Add(new EditPlacementOutline(group.Id, group.States[i].Id, i));

        var edits = project.EditDefinitions.Select(edit => new AuthoredEditOutlineEntry(edit.Id,
            edit.Kind, edit.Target, edit.Label, placements[edit.Id].ToArray(), edit.ReturnWarning)).ToArray();
        var editsByPart = edits.GroupBy(edit => PartKey(edit.Target), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<AuthoredEditOutlineEntry>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var slotsByEdit = new Dictionary<string, IReadOnlyList<EditSlotState>>(StringComparer.Ordinal);
        var slotsByEditAndId = new Dictionary<string, EditSlotState>(StringComparer.Ordinal);
        foreach (var edit in project.EditDefinitions)
        {
            var states = new List<EditSlotState>(edit.Bindings.Count);
            foreach (var binding in edit.Bindings)
            {
                if (!slotsById.TryGetValue(binding.SlotId, out var slot)) continue;
                var asset = binding.ProjectAssetId is { } assetId
                    ? assetsById.GetValueOrDefault(assetId) : null;
                var state = new EditSlotState(slot, binding, asset);
                states.Add(state);
                slotsByEditAndId[EditSlotKey(edit.Id, slot.Id)] = state;
            }
            slotsByEdit[edit.Id] = states;
        }

        var knownParts = project.TargetSlots.Select(slot => slot.Part)
            .Concat(project.EditDefinitions.Select(edit => edit.Target))
            .Concat(project.WorkspaceIndex?.Records.Select(record => record.Part)
                ?? Enumerable.Empty<TargetPart>())
            .GroupBy(PartKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var slotsByPart = project.TargetSlots
            .GroupBy(slot => PartKey(slot.Part), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<TargetSlot>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new EditBoardSnapshot(project, edits, knownParts, slotsByEdit, editsByPart,
            slotsByEditAndId, slotsById, slotsByPart);
    }

    private static string PartKey(TargetPart part) =>
        $"{part.Subject}\u001f{part.Outfit}\u001f{part.RendererSlot}";

    private static string EditSlotKey(string editId, string slotId) => editId + "\u001f" + slotId;
}
