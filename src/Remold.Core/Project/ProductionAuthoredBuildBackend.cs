using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Remold.Core.Project;

/// <summary>Reflection evidence for one exact current material carrier. The resolver that supplies this
/// record is responsible for the whole CANDIDATE FAMILY: every shader variant the game can bind at this
/// material's draws that declares the patched layout — runtime state (shadow quality, fog, LOD, render
/// features) picks among them per machine and per scene, so evidence pinned to one measured variant
/// would ship a patch that silently never fires under other settings. All candidates carry ONE
/// filter-index value; the runtime gate asks "is a declaring variant bound", not "is this one".</summary>
public sealed record MaterialRenderEvidence(
    string ShaderIdentity,
    IReadOnlyList<string> PixelShaderHashes,
    int PixelShaderFilterIndex,
    string MaterialLayout,
    IReadOnlyList<BuildMaterialValueField> Fields,
    string Proof);

/// <summary>The production capability boundary between current-install identity and the runtime compiler.
/// It never emits or mutates intent; an operation without a usable draw discriminator is blocking.</summary>
public sealed class ProductionAuthoredBuildBackend : IAuthoredBuildBackend
{
    private readonly Func<TargetPart, LegacyResolvedPart?> _resolvePart;
    private readonly Func<TargetSlot, MaterialRenderEvidence?>? _materialEvidence;
    private readonly IMaterialGameValueReader _materialValues;
    private readonly Func<TargetSlot, string?>? _meshReplaceBlock;

    /// <param name="meshReplaceBlock">Why the slot's game mesh cannot take replacement geometry, as the
    /// plan verdict's own reason, or null when it can. Asked only of geometry slots with active replacement
    /// work; without it the backend cannot judge the mesh and the build execution's refusal is the
    /// backstop.</param>
    public ProductionAuthoredBuildBackend(Func<TargetPart, LegacyResolvedPart?> resolvePart,
        Func<TargetSlot, MaterialRenderEvidence?>? materialEvidence = null,
        IMaterialGameValueReader? materialValues = null,
        Func<TargetSlot, string?>? meshReplaceBlock = null)
    {
        _resolvePart = resolvePart ?? throw new ArgumentNullException(nameof(resolvePart));
        _materialEvidence = materialEvidence;
        _materialValues = materialValues ?? new MaterialFamilyValueReader();
        _meshReplaceBlock = meshReplaceBlock;
    }

    public BuildSlotResolution ResolveSlot(TargetSlot authoredSlot)
    {
        LegacyResolvedPart? part;
        try { part = _resolvePart(authoredSlot.Part); }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // The read's own exception is a diagnosis of this app's read, not of the mod: it names bundles,
            // path ids and exception types. The line says what failed; the exception goes to the log.
            return new BuildSlotResolution(BuildPlanVerdict.Unresolved, null,
                "couldn't read this part from the game files", e.ToString());
        }
        if (part is null)
            return new BuildSlotResolution(BuildPlanVerdict.Unresolved, null,
                "this part is not in the current game files");

        GameAssetRef renderer = part.Renderer;
        GameAssetRef mesh = part.Mesh;
        string? tier = authoredSlot.Tier;
        bool alternateTier = tier is not null && !string.Equals(tier, "lod0",
            StringComparison.OrdinalIgnoreCase);
        if (alternateTier)
        {
            var tiers = part.Tiers?.Where(candidate => string.Equals(candidate.Tier, tier,
                StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<LegacyResolvedTier>();
            if (tiers.Count != 1)
                return new BuildSlotResolution(tiers.Count == 0 ? BuildPlanVerdict.Unresolved
                        : BuildPlanVerdict.Conflict, null,
                    tiers.Count == 0
                        ? $"this part has no {tier} level of detail in the current game files"
                        : $"this part has {tiers.Count} meshes for its {tier} level of detail in the "
                          + "current game files");
            renderer = tiers[0].Renderer;
            mesh = tiers[0].Mesh;
        }
        if (!Exact(renderer) || !Exact(mesh))
            return new BuildSlotResolution(BuildPlanVerdict.Unresolved, null,
                "this part's mesh could not be identified in the current game files");

        GameAssetRef? material = null;
        int? drawIndexCount = null;
        bool needsGameMaterial = authoredSlot.Domain == TargetSlotDomain.Game
            && (authoredSlot.MaterialSlotIndex is not null
                || authoredSlot.SubmeshIndex is not null && authoredSlot.Input is not
                    (TargetInputKind.Geometry or TargetInputKind.Visibility));
        if (needsGameMaterial && alternateTier)
            return new BuildSlotResolution(BuildPlanVerdict.Unresolved, null,
                $"materials on the {authoredSlot.Tier} level of detail cannot be changed");
        if (needsGameMaterial)
        {
            int index = authoredSlot.MaterialSlotIndex ?? authoredSlot.SubmeshIndex!.Value;
            var matches = part.Materials.Where(candidate =>
                candidate.MaterialSlotIndex == index).ToList();
            if (matches.Count != 1 || !Exact(matches[0].Material))
                return new BuildSlotResolution(matches.Count > 1 ? BuildPlanVerdict.Conflict
                        : BuildPlanVerdict.Unresolved, null,
                    matches.Count == 0
                        ? $"this part has no material {index} in the current game files"
                        : matches.Count > 1
                            ? $"this part has {matches.Count} materials at material {index} in the "
                              + "current game files"
                            : $"material {index} could not be identified in the current game files");
            material = matches[0].Material;
            if (part.MaterialIndexCounts is { } counts && index >= 0)
                drawIndexCount = index < counts.Count ? counts[index] : 0;
        }

        return new BuildSlotResolution(BuildPlanVerdict.Resolved, new TargetSlot
        {
            Id = authoredSlot.Id,
            Part = Clone(authoredSlot.Part),
            Tier = authoredSlot.Tier,
            SubmeshIndex = authoredSlot.SubmeshIndex,
            MaterialSlotIndex = authoredSlot.MaterialSlotIndex,
            Input = authoredSlot.Input,
            ShaderProperty = authoredSlot.ShaderProperty,
            Domain = authoredSlot.Domain,
            Semantic = authoredSlot.Semantic,
            Renderer = Clone(renderer),
            Mesh = Clone(mesh),
            Material = material is null ? null : Clone(material),
            DrawIndexCount = drawIndexCount,
        }, "the structural route resolved to exact current-install objects");
    }

    public BuildOperationResolution ResolveBinding(BuildBindingRequest request)
    {
        if (request.CurrentSlot.Input == TargetInputKind.MaterialValue)
        {
            var evidence = _materialEvidence?.Invoke(request.CurrentSlot);
            if (evidence is null)
                return Block(BuildPlanVerdict.Unsupported,
                    "this material's shader has no adjustable values");
            var render = Render(request.CurrentSlot, Proof(request.CurrentSlot, "material-draw"),
                evidence);
            return MaterialValueBuildSupport.Resolve(request, render, _materialValues);
        }

        BuildRuntimeAction action = request.EffectiveValue.Kind switch
        {
            EffectiveValueKind.ProjectAsset => BuildRuntimeAction.BindProjectAsset,
            EffectiveValueKind.SourceGameSlot => BuildRuntimeAction.BindGameSource,
            EffectiveValueKind.Neutral => BuildRuntimeAction.GenerateNeutral,
            _ => BuildRuntimeAction.None,
        };
        if (action == BuildRuntimeAction.None)
            return Block(BuildPlanVerdict.Unsupported,
                "this value cannot be built into the mod");

        // Replacement geometry is a capability of the GAME mesh, not of the replacement: a mesh the swap
        // routes cannot take (blend shapes, an unreadable skin shape, a spring rig) blocks at plan
        // altitude, where the ③ page can name the edit — the build execution's own refusal stays behind
        // it as the drift backstop.
        if (request.CurrentSlot.Input == TargetInputKind.Geometry
            && request.EffectiveValue.Kind is EffectiveValueKind.ProjectAsset
                or EffectiveValueKind.SourceGameSlot
            && _meshReplaceBlock?.Invoke(request.CurrentSlot) is { } meshBlocked)
            return Block(BuildPlanVerdict.Unsupported, meshBlocked);

        var proof = Targeting(request);
        if (proof.Decision is { } failure) return failure;
        var renderPlan = Render(request.CurrentSlot, proof.Proof!);
        string emissionId = request.RowId + ":runtime";
        var kind = request.EffectiveValue.Kind == EffectiveValueKind.Neutral
            ? BuildEmissionKind.NeutralBinding
            : request.CurrentSlot.Input == TargetInputKind.Geometry
                ? BuildEmissionKind.GeometryReplacement : BuildEmissionKind.ResourceBinding;
        var gate = request.Gate;
        string identity = FunctionalIdentity(request);
        return new BuildOperationResolution(new BuildPlanDecision(BuildPlanVerdict.Resolved, action,
                proof.Proof, proof.Reason), renderPlan,
            new[]
            {
                new BuildRuntimeEmission(emissionId, kind, proof.Proof!, gate,
                    renderPlan.Contracts.Select(contract => contract.Id).ToArray(), proof.Reason),
            },
            new[]
            {
                new BuildOutputArtifact(emissionId + ":output",
                    $"compiled {request.CurrentSlot.Input} runtime resource for "
                        + request.CurrentSlot.Part.RendererSlot,
                    identity, null, true,
                    new[] { emissionId }, "required by the resolved active binding"),
            });
    }

    public BuildOperationResolution ResolveVisibility(BuildVisibilityRequest request)
    {
        var slot = request.CurrentSlot;
        var proof = Proof(slot, "renderer-index-buffer");
        // One part has one suppression account however many states demand it hidden; the plan names the
        // account and the gate carries the whole list of states that ask for it.
        string id = request.Id;
        var gate = request.Gate;
        // Suppression is a complete render account with no contract: nothing draws in the vanilla
        // draw's place, so the only covered role is the suppression target itself.
        var render = new BuildRenderPlan(new[]
        {
            Role(BuildRenderRoleKind.PoseAnchor, false, slot, null,
                "a suppressed part poses no geometry"),
            Role(BuildRenderRoleKind.LayoutTarget, false, slot, null,
                "a suppressed part compiles no replacement layout"),
            Role(BuildRenderRoleKind.RenderCarrier, false, slot, null,
                "no replacement draw rides a carrier"),
            Role(BuildRenderRoleKind.MaterialCarrier, false, slot, null,
                "a suppressed draw inherits no material state"),
            Role(BuildRenderRoleKind.SuppressionTarget, true, slot, proof,
                "the exact renderer's index-buffer section suppresses every submitted draw"),
        }, Array.Empty<RenderContract>(),
            "suppression emits no draw of its own; the vanilla draws drop at their own discriminator");
        return new BuildOperationResolution(new BuildPlanDecision(BuildPlanVerdict.Resolved,
                BuildRuntimeAction.Hide, proof,
                "the exact renderer's index-buffer section suppresses every submitted draw"), render,
            new[]
            {
                new BuildRuntimeEmission(id, BuildEmissionKind.Suppression, proof, gate,
                    Array.Empty<string>(), "suppresses the exact current renderer draw"),
            }, Array.Empty<BuildOutputArtifact>());
    }

    public BuildLifecycleResolution ResolveLifecycle(BuildLifecycleRequest request)
    {
        // A part is switched at runtime when ANY condition it acts under is keyed: its own group's
        // positions, and the states of another group that take it off screen. The launch condition alone
        // answers only for the first, so an always-on part a foreign key hides read as having no toggle
        // while a key was in fact deciding whether it drew.
        bool keyed = !request.LaunchCondition.IsAlways
            || (request.ActingConditions ?? Array.Empty<PlanCondition>())
                .Any(condition => !condition.IsAlways);
        var rows = new List<BuildLifecycleCoverage>
        {
            !keyed
                ? new BuildLifecycleCoverage(BuildLifecycleEvent.Toggle,
                    BuildCoverageState.NotApplicable, BuildLifecycleMechanism.NotApplicable,
                    "the composition has no runtime toggle")
                : new BuildLifecycleCoverage(BuildLifecycleEvent.Toggle,
                    BuildCoverageState.Covered, BuildLifecycleMechanism.KeyGate,
                    "an authored key gates every runtime emission for the part"),
            new(BuildLifecycleEvent.Reload, BuildCoverageState.Covered,
                BuildLifecycleMechanism.ConfigurationReload,
                "configuration reload recreates resources and authored initial state"),
            new(BuildLifecycleEvent.SceneChange, BuildCoverageState.Covered,
                BuildLifecycleMechanism.PerDrawMatch,
                "runtime work is entered only at a matching current draw"),
            new(BuildLifecycleEvent.OutfitChange, BuildCoverageState.Covered,
                BuildLifecycleMechanism.PerDrawMatch,
                "the subject-scoped renderer proof stops matching when the outfit is absent"),
            new(BuildLifecycleEvent.LodChange, BuildCoverageState.Covered,
                BuildLifecycleMechanism.PerDrawMatch,
                "each resolved rendered tier receives its own current draw proof"),
        };
        return new BuildLifecycleResolution(BuildPlanVerdict.Resolved,
            new BuildLifecyclePlan(request.LaunchCondition,
                rows, "runtime state follows the authored composition and current draw lifecycle"),
            "the existing key, reload and per-draw mechanisms cover this operation");
    }

    private (BuildTargetingProof? Proof, string Reason, BuildOperationResolution? Decision)
        Targeting(BuildBindingRequest request)
    {
        var slot = request.CurrentSlot;
        if (slot.Domain == TargetSlotDomain.Game
            && slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
                or TargetInputKind.Blend or TargetInputKind.Texture)
        {
            var resolved = _resolvePart(slot.Part);
            var materials = resolved?.Materials.Where(candidate =>
                candidate.MaterialSlotIndex == slot.MaterialSlotIndex).ToList()
                ?? new List<LegacyResolvedMaterial>();
            var textures = new List<LegacyResolvedTexture>();
            if (materials.Count == 1)
            {
                if (string.IsNullOrWhiteSpace(slot.ShaderProperty))
                {
                    var first = materials[0].Textures.FirstOrDefault(texture => texture.Input == slot.Input);
                    if (first is not null && Exact(first.Texture)) textures.Add(first);
                }
                else
                    textures = materials[0].Textures.Where(texture => texture.Input == slot.Input
                        && string.Equals(texture.ShaderProperty, slot.ShaderProperty, StringComparison.Ordinal)
                        && Exact(texture.Texture)).ToList();
            }
            if (textures.Count != 1)
            {
                string label = Textures.TextureMap.SlotLabel(slot.Input, slot.ShaderProperty);
                return Failure(textures.Count > 1 ? BuildPlanVerdict.Conflict
                        : BuildPlanVerdict.Unresolved,
                    textures.Count == 0
                        ? $"the original material has no {label} in the current game files"
                        : $"the original material has {textures.Count} textures for its {label}, so "
                          + "the build cannot choose one");
            }
            return (new BuildTargetingProof("renderer-index-buffer-and-exact-texture",
                    $"{ObjectId(slot.Renderer)} / {ObjectId(textures[0].Texture)}"),
                "the exact current texture resource is bound at the target renderer draw", null);
        }
        if (slot.Input == TargetInputKind.Ramp && slot.SubmeshIndex is not null
            && request.EffectiveValue.ProjectAsset?.Kind == ProjectAssetKind.Ramp)
        {
            var resolved = _resolvePart(slot.Part);
            var material = resolved?.Materials.SingleOrDefault(candidate =>
                candidate.MaterialSlotIndex == slot.MaterialSlotIndex);
            bool replacement = slot.Domain == TargetSlotDomain.EditOutput;
            if (!replacement)
            {
                if (material is null)
                    return Failure(BuildPlanVerdict.Unresolved,
                        "the original material is not in the current game files");
                var rampTargets = material.Textures.Where(texture => texture.Input == TargetInputKind.Ramp
                    && Exact(texture.Texture)).ToList();
                if (rampTargets.Count != 1)
                    return Failure(rampTargets.Count > 1 ? BuildPlanVerdict.Conflict
                            : BuildPlanVerdict.Unresolved,
                        rampTargets.Count == 0
                            ? "the original material draws without a toon ramp, so one cannot be "
                              + "picked for it"
                            : $"the original material has {rampTargets.Count} toon ramps, so the build "
                              + "cannot choose one");
                var ordinary = material.Textures.Where(texture => texture.Input is
                    TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
                        or TargetInputKind.Blend)
                    .Where(texture => Exact(texture.Texture))
                    .OrderBy(texture => texture.Input switch
                    {
                        TargetInputKind.BaseColor => 0,
                        TargetInputKind.Normal => 1,
                        TargetInputKind.Rmo => 2,
                        TargetInputKind.Blend => 3,
                        _ => 4,
                    }).ToList();
                var unique = ordinary.FirstOrDefault(texture => resolved!.Materials
                    .Where(sibling => sibling.MaterialSlotIndex != material.MaterialSlotIndex)
                    .SelectMany(sibling => sibling.Textures)
                    .All(other => !SameObject(texture.Texture, other.Texture)));
                if (unique is null)
                    return Failure(BuildPlanVerdict.Unsupported, ordinary.Count == 0
                        ? "this material has no base color, normal, RMO or effect map to tell it apart "
                          + "by, so the toon ramp cannot be aimed at it. Pick the toon ramp on another "
                          + "material, or remove it"
                        : "this material shares every one of its textures with another material on the "
                          + "part, so the toon ramp cannot be aimed at it. Pick the toon ramp on another "
                          + "material, or remove it");
                return (new BuildTargetingProof("unique-bound-resource",
                        $"{ObjectId(unique.Texture)} / ramp {ObjectId(rampTargets[0].Texture)} "
                        + $"on {ObjectId(slot.Renderer)}"),
                    "an exact ordinary texture is unique to this material and its exact toon-ramp "
                    + "resource supplies the target register", null);
            }
        }

        string kind = slot.Input switch
        {
            TargetInputKind.Geometry => "renderer-index-buffer-and-draw-range",
            TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
                or TargetInputKind.Blend or TargetInputKind.Texture
                => slot.Domain == TargetSlotDomain.EditOutput
                    ? "replacement-submesh-draw-range"
                    : "renderer-index-buffer-and-exact-texture",
            TargetInputKind.Ramp => "replacement-submesh-draw-range",
            _ => "exact-renderer-draw",
        };
        return (Proof(slot, kind), "the runtime compiler derives the discriminator from exact current objects",
            null);
    }

    private static BuildRenderPlan Render(TargetSlot slot, BuildTargetingProof proof,
        MaterialRenderEvidence? evidence = null)
    {
        bool geometry = slot.Input == TargetInputKind.Geometry;
        var roles = new[]
        {
            Role(BuildRenderRoleKind.PoseAnchor, geometry, slot, null,
                geometry ? "the current target mesh supplies reference pose space"
                    : "this binding does not replace posed geometry"),
            Role(BuildRenderRoleKind.LayoutTarget, geometry, slot, null,
                geometry ? "the replacement is compiled for the current mesh layout"
                    : "this binding retains the current vertex layout"),
            Role(BuildRenderRoleKind.RenderCarrier, true, slot, proof,
                "the current renderer submits the draw that carries this operation"),
            Role(BuildRenderRoleKind.MaterialCarrier, true, slot, null,
                "shader, dynamic constants and render state remain live on the current material carrier"),
            Role(BuildRenderRoleKind.SuppressionTarget, geometry, slot, geometry ? proof : null,
                geometry ? "the replaced vanilla draw is suppressed at the same discriminator"
                    : "this binding does not suppress geometry"),
        };
        var passes = Enum.GetValues<BuildRenderPass>().Select(pass => new BuildPassCoverage(pass,
            evidence is null || pass == BuildRenderPass.Color
                ? BuildCoverageState.Covered : BuildCoverageState.NotApplicable,
            evidence is null
                ? "the operation follows the carrier whenever the game submits this pass"
                : pass == BuildRenderPass.Color
                    ? "the proved pixel shader consumes this value in the visible color pass"
                    : "this semantic patch is not run outside its proved pixel shader pass")).ToArray();
        var contract = new RenderContract(slot.Id + ":draw", slot, slot, proof,
            geometry ? "current mesh input layout" : "live carrier input layout",
            "current renderer draw space", evidence?.ShaderIdentity ?? "live carrier shader",
            evidence?.MaterialLayout ?? "carrier-owned", 0, BuildTransparency.Unknown,
            "live carrier", BuildCullMode.Unknown, passes,
            new BuildVisibilityDomain(new[] { "game-submitted scenes" },
                new[] { slot.Part.Outfit }, new[] { slot.Tier ?? "all rendered tiers" },
                "subject-scoped current draw", "the operation exists only at a matching carrier draw"),
            new BuildCarrierBounds(BuildBoundsBasis.Unavailable, null, null,
                "the runtime compiler cannot enlarge Unity CPU renderer bounds"), evidence?.Fields,
            evidence?.PixelShaderHashes, evidence?.PixelShaderFilterIndex,
            BuildRenderStateOwnership.LiveCarrier,
            "queue, transparency, stencil and cull remain owned by the live game draw");
        return new BuildRenderPlan(roles, new[] { contract }, evidence is null
            ? "the current carrier owns render state and every submitted pass"
            : evidence.Proof);
    }

    private static BuildRenderRole Role(BuildRenderRoleKind kind, bool covered, TargetSlot slot,
        BuildTargetingProof? proof, string reason) => new(kind,
        covered ? BuildCoverageState.Covered : BuildCoverageState.NotApplicable,
        covered ? slot : null, covered ? proof : null, reason);

    private static BuildOperationResolution Block(BuildPlanVerdict verdict, string reason) => new(
        BuildPlanDecision.Blocked(verdict, reason), null,
        Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>());

    private static (BuildTargetingProof?, string, BuildOperationResolution?) Failure(
        BuildPlanVerdict verdict, string reason) => (null, reason, Block(verdict, reason));

    private static BuildTargetingProof Proof(TargetSlot slot, string kind) =>
        new(kind, ObjectId(slot.Renderer)
            + (slot.Input == TargetInputKind.Geometry && slot.Mesh is not null
                ? " / " + ObjectId(slot.Mesh) : "")
            + (slot.SubmeshIndex is { } submesh
            ? $" / submesh {submesh}" : ""));

    private static string FunctionalIdentity(BuildBindingRequest request) =>
        $"{request.CurrentSlot.Input}:{request.EffectiveValue.Kind}:"
        + (request.EffectiveValue.ProjectAsset?.Id
            ?? request.EffectiveValue.SourceGameSlot?.Id ?? request.RowId);

    private static bool Exact(GameAssetRef? value) => value is not null
        && !string.IsNullOrWhiteSpace(value.GameBuild)
        && !string.IsNullOrWhiteSpace(value.LogicalBundle) && value.PathId != 0;

    private static bool SameObject(GameAssetRef left, GameAssetRef right) =>
        string.Equals(left.GameBuild, right.GameBuild, StringComparison.Ordinal)
        && string.Equals(left.LogicalBundle, right.LogicalBundle, StringComparison.Ordinal)
        && left.PathId == right.PathId;

    private static string ObjectId(GameAssetRef value) =>
        $"{value.GameBuild}:{value.LogicalBundle}:{value.PathId}";

    private static TargetPart Clone(TargetPart source) => new()
    {
        Subject = source.Subject,
        Outfit = source.Outfit,
        RendererSlot = source.RendererSlot,
    };

    private static GameAssetRef Clone(GameAssetRef source) => new()
    {
        GameBuild = source.GameBuild,
        LogicalBundle = source.LogicalBundle,
        PathId = source.PathId,
        Name = source.Name,
    };
}
