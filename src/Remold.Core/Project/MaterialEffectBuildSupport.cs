using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

public sealed record MaterialEffectBufferPatch(int ConstantBufferSlot, int ByteWidth,
    IReadOnlyList<MaterialPatchWrite> Writes);

public sealed record MaterialEffectTexture(int Slot, float R, float G, float B, float A);

public sealed record BuildMaterialEffectRequest(string RowId, string EditDefinitionId,
    TargetSlot AuthoredSlot, TargetSlot CurrentSlot, string EffectId, BuildEmissionGate Gate);

public static class MaterialEffectBuildSupport
{
    public static BuildOperationResolution Resolve(BuildMaterialEffectRequest request,
        BuildRenderPlan render, IReadOnlyList<MaterialEffectOperation>? operations)
    {
        if (render.Contracts is not { Count: > 0 })
            return new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Conflict,
                AuthoredBuildPlanner.InternalGuard, "the effect operation has no material draw contract"), render,
                Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>());
        var selected = operations?.Where(operation => operation.EffectId == request.EffectId).ToArray();
        if (selected is not { Length: > 0 })
            return new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported,
                $"{Label(request.EffectId)} cannot be disabled on this material."), render,
                Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>());
        var emissions = new List<BuildRuntimeEmission>();
        foreach (var contract in render.Contracts)
        for (int index = 0; index < selected.Length; index++)
        {
            var operation = selected[index];
            var errors = Errors(operation);
            if (errors.Count > 0)
                return new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Conflict,
                    AuthoredBuildPlanner.InternalGuard, string.Join("; ", errors)), render,
                    Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>());
            emissions.Add(new BuildRuntimeEmission(request.RowId + ":" + contract.Id + ":" + index,
                BuildEmissionKind.MaterialEffect, contract.TargetingProof, request.Gate,
                new[] { contract.Id }, "disables the effect at its exact material draw", MaterialEffect: operation));
        }
        return new BuildOperationResolution(new BuildPlanDecision(BuildPlanVerdict.Resolved,
            BuildRuntimeAction.BindProjectAsset, emissions[0].TargetingProof,
            "disables the effect while retaining its authored values"), render, emissions,
            Array.Empty<BuildOutputArtifact>());
    }

    private static string Label(string effectId) =>
        MaterialEffectCatalog.Definition(effectId)?.Label ?? effectId;

    internal static IReadOnlyList<string> Errors(MaterialEffectOperation operation)
    {
        var errors = new List<string>();
        if (operation.PixelShaderHashes is not { Count: > 0 })
            errors.Add("effect operation names no exact shader program");
        else if (operation.PixelShaderHashes.Any(hash => hash is not { Length: 16 } || !hash.All(Uri.IsHexDigit))
                 || operation.PixelShaderHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    != operation.PixelShaderHashes.Count)
            errors.Add("effect operation has a malformed or repeated shader hash");
        if (string.IsNullOrWhiteSpace(operation.EffectId)) errors.Add("effect operation has no effect identity");
        if (operation.Buffers is null || operation.Textures is null)
        { errors.Add("effect operation is incomplete"); return errors; }
        if (!operation.SkipDraw && operation.Buffers.Count == 0 && operation.Textures.Count == 0)
            errors.Add("effect operation has no disable operation");
        if (operation.SkipDraw && (operation.Buffers.Count != 0 || operation.Textures.Count != 0))
            errors.Add("a skipped draw also carries resource writes");
        var slots = new HashSet<int>();
        foreach (var buffer in operation.Buffers)
        {
            if (buffer.ConstantBufferSlot is < 0 or > 13 || !slots.Add(buffer.ConstantBufferSlot))
                errors.Add("effect operation has an invalid or repeated constant-buffer slot");
            if (buffer.ByteWidth <= 0 || buffer.ByteWidth % 16 != 0)
                errors.Add("effect operation has an invalid constant-buffer width");
            if (buffer.Writes is not { Count: > 0 }) { errors.Add("effect buffer has no writes"); continue; }
            var offsets = new HashSet<int>();
            foreach (var write in buffer.Writes)
                if (!float.IsFinite(write.Value) || write.ByteOffset < 0 || write.ByteOffset % 4 != 0
                    || write.ByteOffset + 4 > buffer.ByteWidth || !offsets.Add(write.ByteOffset))
                    errors.Add("effect buffer has an invalid or repeated write");
        }
        slots.Clear();
        foreach (var texture in operation.Textures)
            if (texture.Slot is < 0 or > 127 || !slots.Add(texture.Slot)
                || new[] { texture.R, texture.G, texture.B, texture.A }.Any(value => !float.IsFinite(value)
                    || value < 0 || value > 1)) errors.Add("effect operation has an invalid texture override");
        return errors;
    }
}
