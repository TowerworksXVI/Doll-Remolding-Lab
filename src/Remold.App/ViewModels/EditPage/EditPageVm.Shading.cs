using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Remold.Core.Project;

namespace Remold.App.ViewModels.EditPage;

public sealed partial class EditPageVm
{
    private EditShadingRowVm WithShadingEffects(EditShadingRowVm row)
    {
        var read = _shell.PeekShading(row.Part, row.MaterialSlotIndex, row.Material);
        row.EffectsNote = read is null ? "Reading material effects…"
            : read.Problem ?? (read.Info is null ? "No adjustable effects are available for this material." : "");
        var info = read?.Info;
        if (info is null) return row;
        row.EffectOperations = info.EffectOperations ?? Array.Empty<MaterialEffectOperation>();
        var present = (info.Effects ?? Array.Empty<EditShadingEffect>()).Select(effect => effect.Id)
            .ToHashSet(StringComparer.Ordinal);
        row.SetEffects((info.Effects ?? Array.Empty<EditShadingEffect>()).Select(effect => effect with
        {
            IsEnabled = !row.DisabledEffectIds.Contains(effect.Id),
            IsEdited = row.AuthoredValues.Keys.Any(semantic =>
                MaterialEffectCatalog.EffectForSemantic(semantic, present) == effect.Id),
        }));
        return row;
    }

    private async Task<EditShadingInfo?> ReadShadingEffectsAsync(EditShadingRowVm row)
    {
        try
        {
            var info = await _shell.ReadShadingAsync(row.Part, row.MaterialSlotIndex, row.Material);
            row.EffectsNote = info is null ? "No adjustable effects are available for this material." : "";
            return info;
        }
        catch (Exception failure)
        {
            row.EffectsNote = failure is EditShadingFailureException ? failure.Message
                : "Couldn't read this material's effects.";
            return null;
        }
    }

    [RelayCommand]
    private async Task ToggleShadingEffect(EditShadingEffectRowVm? effect)
    {
        if (effect is null || _session is null) return;
        var row = effect.Owner;
        bool enabled = effect.IsEnabled;
        if (enabled == effect.OriginalIsEnabled) return;
        CommitPendingRename();
        string gate = ShadingBusy(row);
        if (!Take(gate, ShadingIsBusy(row)))
        {
            effect.ResetEnabled();
            return;
        }
        try
        {
            var info = enabled ? null : await ReadShadingEffectsAsync(row);
            if (!enabled && info?.EffectOperations is null) return;
            var session = _session;
            WriteShading(row, "change this effect", edit =>
            {
                session.ApplyMaterialShading(edit.EditDefinitionId, row.Part, row.MaterialSlotIndex,
                    Array.Empty<AuthoredMaterialValueEdit>(),
                    new[] { new AuthoredMaterialEffectEdit(effect.Id, enabled) },
                    _shell.ResolvePart, info?.EffectOperations);
                return $"{(enabled ? "Enabled" : "Disabled")} {effect.Label}.";
            });
        }
        finally
        {
            effect.ResetEnabled();
            Release(gate);
        }
    }

    [RelayCommand]
    private async Task DisableAllShadingEffects(EditShadingRowVm? row)
    {
        if (row is null || _session is null) return;
        CommitPendingRename();
        string gate = ShadingBusy(row);
        if (!Take(gate, ShadingIsBusy(row))) return;
        try
        {
            var info = await ReadShadingEffectsAsync(row);
            if (info?.EffectOperations is null) return;
            // A parent's off switch already suppresses its children; their authored choices are retained.
            var effects = (info.Effects ?? Array.Empty<EditShadingEffect>())
                .Where(effect => effect.ParentId is null && !row.DisabledEffectIds.Contains(effect.Id))
                .Select(effect => new AuthoredMaterialEffectEdit(effect.Id, false)).ToArray();
            if (effects.Length == 0)
            {
                Status = "All removable effects are already disabled.";
                return;
            }
            var session = _session;
            WriteShading(row, "disable these effects", edit =>
            {
                session.ApplyMaterialShading(edit.EditDefinitionId, row.Part, row.MaterialSlotIndex,
                    Array.Empty<AuthoredMaterialValueEdit>(), effects, _shell.ResolvePart,
                    info.EffectOperations);
                return "Disabled all removable effects.";
            });
        }
        finally { Release(gate); }
    }
}
