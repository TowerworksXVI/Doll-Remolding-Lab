using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

public enum BuildCoverageState
{
    Covered,
    NotApplicable,
    Unsupported,
    Unresolved,
    NeedsRepair,
    Conflict,
}

public enum BuildRenderRoleKind
{
    PoseAnchor,
    LayoutTarget,
    RenderCarrier,
    MaterialCarrier,
    SuppressionTarget,
}

public enum BuildRenderPass
{
    Color,
    Outline,
    Shadow,
    Reflection,
    Transparency,
    SpecialView,
}

public enum BuildTransparency
{
    Unknown,
    Opaque,
    Cutout,
    Transparent,
}

public enum BuildCullMode
{
    Unknown,
    Off,
    Front,
    Back,
}

public enum BuildRenderStateOwnership
{
    Measured,
    LiveCarrier,
}

public enum BuildBoundsBasis
{
    Known,
    Estimated,
    Unavailable,
}

public enum BuildLifecycleEvent
{
    Toggle,
    Reload,
    SceneChange,
    OutfitChange,
    LodChange,
}

public enum BuildLifecycleMechanism
{
    Unknown,
    NotApplicable,
    KeyGate,
    ConfigurationReload,
    PerDrawMatch,
    RuntimeReset,
}

public enum BuildEmissionKind
{
    GeometryReplacement,
    ResourceBinding,
    NeutralBinding,
    Suppression,
    MaterialValuePatch,
}

/// <summary>One independently named role in a runtime operation. Not-applicable is explicit so a
/// texture bind cannot accidentally acquire a geometry pose premise.</summary>
public sealed record BuildRenderRole(
    BuildRenderRoleKind Kind,
    BuildCoverageState State,
    TargetSlot? CurrentSlot,
    BuildTargetingProof? TargetingProof,
    string Reason);

public sealed record BuildPassCoverage(
    BuildRenderPass Pass,
    BuildCoverageState State,
    string Reason);

/// <summary>The current-install domain in which a carrier is expected to submit its draw.</summary>
public sealed record BuildVisibilityDomain(
    IReadOnlyList<string> Scenes,
    IReadOnlyList<string> OutfitStates,
    IReadOnlyList<string> Tiers,
    string InstanceScope,
    string Reason);

/// <summary>Carrier bounds in its current draw space. Unavailable is an honest derived boundary, not
/// an invented zero-sized box.</summary>
public sealed record BuildCarrierBounds(
    BuildBoundsBasis Basis,
    IReadOnlyList<float>? Min,
    IReadOnlyList<float>? Max,
    string Reason);

/// <summary>One semantic field proved by reflection for the active shader variant. Buffer width is
/// not enough evidence: variants with the same UnityPerMaterial shape may omit a field.</summary>
public sealed record BuildMaterialValueField(
    string Semantic,
    int ConstantBufferSlot,
    int ByteOffset,
    string Proof);

/// <summary>The current-install rendering facts for one emitted material/submesh draw.</summary>
public sealed record RenderContract(
    string Id,
    TargetSlot CarrierSlot,
    TargetSlot MaterialCarrierSlot,
    BuildTargetingProof TargetingProof,
    string InputLayout,
    string DrawSpace,
    string ShaderFamily,
    string MaterialLayout,
    int RenderQueue,
    BuildTransparency Transparency,
    string Stencil,
    BuildCullMode Cull,
    IReadOnlyList<BuildPassCoverage> Passes,
    BuildVisibilityDomain Visibility,
    BuildCarrierBounds Bounds,
    IReadOnlyList<BuildMaterialValueField>? MaterialValueFields = null,
    IReadOnlyList<string>? PixelShaderHashes = null,
    int? PixelShaderFilterIndex = null,
    BuildRenderStateOwnership RenderStateOwnership = BuildRenderStateOwnership.Measured,
    string? RenderStateReason = null);

/// <summary>Separate role assignments plus per-draw contracts for one runtime operation.</summary>
public sealed record BuildRenderPlan(
    IReadOnlyList<BuildRenderRole> Roles,
    IReadOnlyList<RenderContract> Contracts,
    string Reason)
{
    public bool BlocksBuild => Roles.Any(r => Blocks(r.State))
        || Contracts.SelectMany(c => c.Passes).Any(p => Blocks(p.State));

    internal static bool Blocks(BuildCoverageState state) => state is
        BuildCoverageState.Unsupported or BuildCoverageState.Unresolved
        or BuildCoverageState.NeedsRepair or BuildCoverageState.Conflict;
}

public sealed record BuildRuntimeEmission(
    string Id,
    BuildEmissionKind Kind,
    BuildTargetingProof TargetingProof,
    BuildEmissionGate Gate,
    IReadOnlyList<string> RenderContractIds,
    string Reason,
    MaterialConstantBufferPatch? MaterialPatch = null);

/// <summary>One ordinal condition: the named key group stands in the named state. <see cref="Always"/> is
/// the keyless term of a part no key can switch, which holds in every session.</summary>
public sealed record BuildGateTerm(string? GroupId, string? Key, int StateIndex)
{
    public static BuildGateTerm Always { get; } = new(null, null, 0);

    public bool IsAlways => GroupId is null;

    /// <summary>Two terms are provably exclusive only when one key decides both: a key stands in exactly
    /// one state at a time. Terms of different groups can hold together, so they are never exclusive.</summary>
    public bool ExcludedBy(BuildGateTerm other) => !IsAlways && !other.IsAlways
        && string.Equals(GroupId, other.GroupId, StringComparison.Ordinal)
        && StateIndex != other.StateIndex;

    public override string ToString() => IsAlways ? "always" : $"{Key}={StateIndex}";
}

/// <summary>When one runtime emission acts: any <see cref="ActiveWhen"/> term holds and no
/// <see cref="UnlessAny"/> term does. A content emission names one ordinal term and carries every state
/// that hides its part as an exception, so hidden outranks content. A suppression names the whole OR-list
/// of states demanding its part hidden and takes no exception: stacked hides cannot disagree.</summary>
public sealed record BuildEmissionGate(
    IReadOnlyList<BuildGateTerm> ActiveWhen,
    IReadOnlyList<BuildGateTerm> UnlessAny)
{
    public BuildEmissionGate(BuildGateTerm activeWhen)
        : this(new[] { activeWhen }, Array.Empty<BuildGateTerm>()) { }

    public static BuildEmissionGate Unconditional { get; } = new(BuildGateTerm.Always);

    /// <summary>Whether two gated emissions can ever act in the same session state. Only a shared key
    /// standing in different states proves they cannot; anything else may overlap.</summary>
    public bool ProvablyExclusiveOf(BuildEmissionGate other)
    {
        if (ActiveWhen is not { Count: > 0 } mine || other.ActiveWhen is not { Count: > 0 } theirs)
            return false;
        return mine.All(left => theirs.All(left.ExcludedBy));
    }

    internal bool SameTermsAs(BuildEmissionGate other) =>
        Same(ActiveWhen, other.ActiveWhen) && Same(UnlessAny, other.UnlessAny);

    private static bool Same(IReadOnlyList<BuildGateTerm>? left, IReadOnlyList<BuildGateTerm>? right)
    {
        var mine = Sorted(left);
        var theirs = Sorted(right);
        return mine.Count == theirs.Count && mine.SequenceEqual(theirs);
    }

    /// <summary>The comparable form of a term list. A missing group or key is not the empty one: the keyless
    /// term holds in every session, while a term naming an empty group names nothing, and collapsing the two
    /// would let the second pass for the first.</summary>
    private static List<(bool Keyed, string Group, bool Named, string Key, int State)> Sorted(
        IReadOnlyList<BuildGateTerm>? terms) => (terms ?? Array.Empty<BuildGateTerm>())
            .Select(term => (Keyed: term.GroupId is not null, Group: term.GroupId ?? "",
                Named: term.Key is not null, Key: term.Key ?? "", State: term.StateIndex))
            .OrderBy(term => term.Keyed)
            .ThenBy(term => term.Group, StringComparer.Ordinal)
            .ThenBy(term => term.Named)
            .ThenBy(term => term.Key, StringComparer.Ordinal)
            .ThenBy(term => term.State).ToList();

    public override string ToString() => string.Join(" or ", ActiveWhen)
        + (UnlessAny is { Count: > 0 } ? " unless " + string.Join(" or ", UnlessAny) : "");
}

/// <summary>One emitted Build resource or generated/copied file. <see cref="File"/> is null for a logical
/// runtime resource with no standalone path. Empty outputs are explicit on operations such as Hide;
/// null output lists mean the backend never accounted for packaging.</summary>
public sealed record BuildOutputArtifact(
    string Id,
    string Purpose,
    string FunctionalIdentity,
    string? File,
    bool Included,
    IReadOnlyList<string> EmissionIds,
    string Reason);

/// <summary>A capability verdict and the rendering/emission account behind it. A resolved runtime action
/// must carry complete non-blocking records; a blocking verdict may carry partial records to explain the
/// gap.</summary>
public sealed record BuildOperationResolution(
    BuildPlanDecision Decision,
    BuildRenderPlan? RenderPlan,
    IReadOnlyList<BuildRuntimeEmission>? Emissions = null,
    IReadOnlyList<BuildOutputArtifact>? OutputArtifacts = null);

/// <summary>One suppression account. <see cref="Id"/> is the plan's name for it — a part has one account
/// however many states demand it hidden, and a Hide edit's binding has its own — so two accounts on one
/// part can never collide into one emission.</summary>
public sealed record BuildVisibilityRequest(
    TargetPart Target,
    string Id,
    TargetSlot AuthoredSlot,
    TargetSlot CurrentSlot,
    BuildEmissionGate Gate);

public sealed record BuildLifecycleCoverage(
    BuildLifecycleEvent Event,
    BuildCoverageState State,
    BuildLifecycleMechanism Mechanism,
    string Reason);

public sealed record BuildLifecyclePlan(
    PlanCondition InitialCondition,
    IReadOnlyList<BuildLifecycleCoverage> Coverage,
    string Reason)
{
    public bool BlocksBuild => Coverage.Any(c => BuildRenderPlan.Blocks(c.State));

    /// <summary>The launch state in the two-state vocabulary the shipped runtime compiler still speaks. A
    /// group cycling more states than that launches somewhere this cannot name, and the layers that read
    /// it refuse such a plan rather than acting on a wrong answer here.</summary>
    public BuildPlanState InitialState => InitialCondition.IsAlways || InitialCondition.StateIndex == 0
        ? BuildPlanState.Active : BuildPlanState.ToggleOff;
}

/// <summary>One part's lifecycle question. <see cref="LaunchCondition"/> is the condition that holds at
/// load, because every key resets to its group's start state each session, and
/// <see cref="ActingConditions"/> names every condition under which the part actually runs something.</summary>
public sealed record BuildLifecycleRequest(
    TargetPart Target,
    PlannedPartDisposition Disposition,
    PlanCondition LaunchCondition,
    IReadOnlyList<PlanCondition> ActingConditions);

/// <summary><inheritdoc cref="BuildPlanDecision" path="/summary/para"/></summary>
public sealed record BuildLifecycleResolution(
    BuildPlanVerdict Verdict,
    BuildLifecyclePlan? Plan,
    string Reason,
    string? Detail = null)
{
    public bool BlocksBuild => Verdict is BuildPlanVerdict.Unsupported or BuildPlanVerdict.Unresolved
        or BuildPlanVerdict.NeedsRepair or BuildPlanVerdict.Conflict
        || Plan?.BlocksBuild == true;
}

internal static class AuthoredRenderPlanValidator
{
    internal static IReadOnlyList<string> OperationErrors(BuildOperationResolution resolution,
        BuildEmissionKind expectedKind, BuildEmissionGate gate,
        bool requireComplete)
    {
        var errors = new List<string>();
        var emissions = resolution.Emissions;
        var outputs = resolution.OutputArtifacts;
        if (emissions is null)
        {
            if (requireComplete) errors.Add("runtime emissions were not accounted for");
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var contracts = resolution.RenderPlan?.Contracts.ToDictionary(c => c.Id,
                StringComparer.Ordinal) ?? new Dictionary<string, RenderContract>(StringComparer.Ordinal);
            var contractIds = contracts.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var emission in emissions)
            {
                string at = string.IsNullOrWhiteSpace(emission.Id)
                    ? "runtime emission" : $"runtime emission '{emission.Id}'";
                if (string.IsNullOrWhiteSpace(emission.Id)) errors.Add("runtime emission has no id");
                else if (!ids.Add(emission.Id)) errors.Add($"duplicate runtime emission id '{emission.Id}'");
                if (!Enum.IsDefined(emission.Kind)) errors.Add($"{at} has an unknown kind");
                if (!Proof(emission.TargetingProof)) errors.Add($"{at} has no targeting proof");
                ValidateGate(emission.Gate, gate, at, errors);
                if (string.IsNullOrWhiteSpace(emission.Reason)) errors.Add($"{at} has no reason");
                if (emission.Kind == BuildEmissionKind.MaterialValuePatch)
                {
                    if (emission.MaterialPatch is null) errors.Add($"{at} has no material patch");
                    else foreach (string error in MaterialValuePatchValidator.Errors(emission.MaterialPatch))
                        errors.Add($"{at} {error}");
                }
                else if (emission.MaterialPatch is not null)
                    errors.Add($"{at} carries a material patch for {emission.Kind}");
                if (emission.RenderContractIds is null)
                    errors.Add($"{at} has no render-contract account");
                else
                {
                    if (emission.Kind != BuildEmissionKind.Suppression
                        && emission.RenderContractIds.Count == 0)
                        errors.Add($"{at} names no render contracts");
                    foreach (string contractId in emission.RenderContractIds)
                    {
                        if (!contractIds.Contains(contractId))
                            errors.Add($"{at} names missing render contract '{contractId}'");
                        else if (emission.MaterialPatch is { } patch)
                        {
                            var contract = contracts[contractId];
                            if (!string.Equals(contract.MaterialLayout, patch.Layout,
                                    StringComparison.Ordinal))
                                errors.Add($"{at} material patch disagrees with render contract '{contractId}'");
                            foreach (var write in patch.Writes ?? Array.Empty<MaterialPatchWrite>())
                            {
                                var fields = contract.MaterialValueFields?.Where(field =>
                                    string.Equals(field.Semantic, write.Semantic,
                                        StringComparison.Ordinal)).ToList()
                                    ?? new List<BuildMaterialValueField>();
                                if (fields.Count != 1
                                    || fields[0].ConstantBufferSlot != patch.ConstantBufferSlot
                                    || fields[0].ByteOffset != write.ByteOffset)
                                    errors.Add($"{at} material patch is not proved by render contract "
                                        + $"'{contractId}' for '{write.Semantic}'");
                            }
                        }
                    }
                }
            }
            if (requireComplete && !emissions.Any(e => e.Kind == expectedKind))
                errors.Add($"no {expectedKind} emission represents the resolved action");
            if (requireComplete && !emissions.Any(e => SameProof(e.TargetingProof,
                resolution.Decision.TargetingProof)))
                errors.Add("no runtime emission carries the resolved action's targeting proof");
        }

        if (outputs is null)
        {
            if (requireComplete) errors.Add("output artifacts were not accounted for");
        }
        else
        {
            var outputIds = new HashSet<string>(StringComparer.Ordinal);
            var emissionIds = emissions?.Select(e => e.Id).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var output in outputs)
            {
                string at = string.IsNullOrWhiteSpace(output.Id)
                    ? "output artifact" : $"output artifact '{output.Id}'";
                if (string.IsNullOrWhiteSpace(output.Id)) errors.Add("output artifact has no id");
                else if (!outputIds.Add(output.Id)) errors.Add($"duplicate output artifact id '{output.Id}'");
                if (string.IsNullOrWhiteSpace(output.Purpose)) errors.Add($"{at} has no purpose");
                if (string.IsNullOrWhiteSpace(output.FunctionalIdentity))
                    errors.Add($"{at} has no functional identity");
                if (string.IsNullOrWhiteSpace(output.Reason)) errors.Add($"{at} has no reason");
                if (output.File is { } file && !AuthoredProjectValidator.IsProjectRelativeFile(file))
                    errors.Add($"{at} has an invalid output path");
                if (output.EmissionIds is null)
                    errors.Add($"{at} has no emission-consumer account");
                else
                {
                    if (output.Included && output.EmissionIds.Count == 0)
                        errors.Add($"{at} has no emission consumers");
                    foreach (string emissionId in output.EmissionIds)
                        if (!emissionIds.Contains(emissionId))
                            errors.Add($"{at} names missing runtime emission '{emissionId}'");
                }
            }
            if (requireComplete && expectedKind != BuildEmissionKind.Suppression
                && !outputs.Any(o => o.Included))
                errors.Add("the resolved action has no included output artifact");
            if (!requireComplete && outputs.Any(o => o.Included))
                errors.Add("a blocking operation claims an included output artifact");
        }
        return errors;
    }

    internal static IReadOnlyList<string> Errors(BuildRenderPlan plan, bool requireComplete = true)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Reason)) errors.Add("render plan has no reason");
        if (plan.Roles is not { } allRoles)
        {
            errors.Add("render plan has no roles");
            return errors;
        }
        if (plan.Contracts is not { } contracts)
        {
            errors.Add("render plan has no contracts");
            return errors;
        }

        foreach (var kind in Enum.GetValues<BuildRenderRoleKind>())
        {
            var roles = allRoles.Where(r => r.Kind == kind).ToList();
            if (requireComplete && roles.Count == 0)
            {
                errors.Add($"render plan does not account for {kind}");
                continue;
            }
            if (kind is BuildRenderRoleKind.PoseAnchor or BuildRenderRoleKind.LayoutTarget
                && roles.Count > 1)
                errors.Add($"render plan accounts for {kind} {roles.Count} times");
            if (roles.Any(r => r.State == BuildCoverageState.NotApplicable) && roles.Count != 1)
                errors.Add($"not-applicable {kind} cannot be mixed with assigned roles");
        }

        foreach (var role in allRoles)
        {
            if (string.IsNullOrWhiteSpace(role.Reason))
                errors.Add($"{role.Kind} has no reason");
            if (!Enum.IsDefined(role.Kind)) errors.Add("render plan has an unknown role kind");
            if (!Enum.IsDefined(role.State)) errors.Add($"{role.Kind} has an unknown coverage state");
            if (role.State == BuildCoverageState.Covered)
            {
                if (role.CurrentSlot is null) errors.Add($"{role.Kind} has no current slot");
                if (role.Kind is BuildRenderRoleKind.RenderCarrier or BuildRenderRoleKind.SuppressionTarget
                    && !Proof(role.TargetingProof))
                    errors.Add($"{role.Kind} has no targeting proof");
            }
            else if (role.State == BuildCoverageState.NotApplicable)
            {
                if (role.CurrentSlot is not null || role.TargetingProof is not null)
                    errors.Add($"not-applicable {role.Kind} carries a target");
            }
        }

        bool hasRenderCarrier = allRoles.Any(r => r.Kind == BuildRenderRoleKind.RenderCarrier
            && r.State == BuildCoverageState.Covered);
        if (hasRenderCarrier && contracts.Count == 0)
            errors.Add("render plan has a carrier but no draw contracts");
        if (!hasRenderCarrier && contracts.Count > 0)
            errors.Add("render plan has draw contracts but no carrier");
        var contractIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            string at = string.IsNullOrWhiteSpace(contract.Id) ? "render contract" : $"render contract '{contract.Id}'";
            if (!contractIds.Add(contract.Id)) errors.Add($"duplicate render contract id '{contract.Id}'");
            if (string.IsNullOrWhiteSpace(contract.Id)) errors.Add("render contract has no id");
            if (contract.CarrierSlot is null) errors.Add($"{at} has no carrier slot");
            if (contract.MaterialCarrierSlot is null) errors.Add($"{at} has no material-carrier slot");
            if (!Proof(contract.TargetingProof)) errors.Add($"{at} has no targeting proof");
            Required(contract.InputLayout, "input layout");
            Required(contract.DrawSpace, "draw space");
            Required(contract.ShaderFamily, "shader family");
            Required(contract.MaterialLayout, "material layout");
            if (!Enum.IsDefined(contract.RenderStateOwnership))
                errors.Add($"{at} has invalid render-state ownership");
            if (contract.RenderStateOwnership == BuildRenderStateOwnership.LiveCarrier)
            {
                Required(contract.RenderStateReason ?? "", "live-carrier render-state reason");
                if (!Enum.IsDefined(contract.Transparency))
                    errors.Add($"{at} has an invalid transparency value");
                if (!Enum.IsDefined(contract.Cull)) errors.Add($"{at} has an invalid cull mode");
            }
            else
            {
                Required(contract.Stencil, "stencil account");
                if (contract.Transparency == BuildTransparency.Unknown)
                    errors.Add($"{at} has unknown transparency");
                else if (!Enum.IsDefined(contract.Transparency))
                    errors.Add($"{at} has an invalid transparency value");
                if (contract.Cull == BuildCullMode.Unknown) errors.Add($"{at} has unknown cull mode");
                else if (!Enum.IsDefined(contract.Cull)) errors.Add($"{at} has an invalid cull mode");
            }
            ValidatePasses(contract.Passes, at, errors);
            ValidateVisibility(contract.Visibility, at, errors);
            ValidateBounds(contract.Bounds, at, errors);
            ValidateMaterialValueFields(contract.MaterialValueFields, at, errors);

            void Required(string value, string field)
            {
                if (string.IsNullOrWhiteSpace(value)) errors.Add($"{at} has no {field}");
            }
        }

        var carrierIds = allRoles.Where(r => r.Kind == BuildRenderRoleKind.RenderCarrier
                && r.State == BuildCoverageState.Covered && r.CurrentSlot is not null)
            .Select(r => r.CurrentSlot!.Id).ToHashSet(StringComparer.Ordinal);
        var materialCarrierIds = allRoles.Where(r => r.Kind == BuildRenderRoleKind.MaterialCarrier
                && r.State == BuildCoverageState.Covered && r.CurrentSlot is not null)
            .Select(r => r.CurrentSlot!.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in carrierIds)
            if (!contracts.Any(c => c.CarrierSlot is not null
                && string.Equals(c.CarrierSlot.Id, id, StringComparison.Ordinal)))
                errors.Add($"render carrier '{id}' has no draw contract");
        foreach (string id in materialCarrierIds)
            if (!contracts.Any(c => c.MaterialCarrierSlot is not null
                && string.Equals(c.MaterialCarrierSlot.Id, id, StringComparison.Ordinal)))
                errors.Add($"material carrier '{id}' has no draw contract");
        foreach (var contract in contracts)
        {
            if (contract.CarrierSlot is not null && !carrierIds.Contains(contract.CarrierSlot.Id))
                errors.Add($"render contract '{contract.Id}' names an unassigned render carrier");
            if (contract.MaterialCarrierSlot is not null
                && !materialCarrierIds.Contains(contract.MaterialCarrierSlot.Id))
                errors.Add($"render contract '{contract.Id}' names an unassigned material carrier");
        }
        return errors;
    }

    /// <param name="acting">Every condition under which the part actually runs something. A part switches at
    /// runtime when ANY of them is keyed — its own group's positions, and the states of other groups that
    /// take it off screen. Judging that on the launch condition alone called an always-on part a foreign key
    /// hides "no toggle", and then refused the backend that answered otherwise.</param>
    internal static IReadOnlyList<string> LifecycleErrors(BuildLifecyclePlan plan,
        PlanCondition launch, IReadOnlyList<PlanCondition> acting, bool requireComplete = true)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Reason)) errors.Add("lifecycle plan has no reason");
        if (plan.Coverage is null)
        {
            errors.Add("lifecycle plan has no coverage");
            return errors;
        }
        foreach (var kind in Enum.GetValues<BuildLifecycleEvent>())
        {
            var rows = plan.Coverage.Where(c => c.Event == kind).ToList();
            if (rows.Count > 1 || (requireComplete && rows.Count == 0))
                errors.Add($"lifecycle plan accounts for {kind} {rows.Count} times");
        }
        foreach (var row in plan.Coverage)
        {
            if (string.IsNullOrWhiteSpace(row.Reason)) errors.Add($"{row.Event} has no reason");
            if (!Enum.IsDefined(row.Event)) errors.Add("lifecycle plan has an unknown event");
            if (!Enum.IsDefined(row.State)) errors.Add($"{row.Event} has an unknown coverage state");
            if (!Enum.IsDefined(row.Mechanism) || row.Mechanism == BuildLifecycleMechanism.Unknown)
                errors.Add($"{row.Event} has no lifecycle mechanism");
            if (row.State == BuildCoverageState.NotApplicable
                && row.Mechanism != BuildLifecycleMechanism.NotApplicable)
                errors.Add($"{row.Event} is not applicable but names {row.Mechanism}");
            if (row.State == BuildCoverageState.Covered
                && row.Mechanism == BuildLifecycleMechanism.NotApplicable)
                errors.Add($"{row.Event} is covered without a lifecycle mechanism");
        }
        var toggleCoverage = plan.Coverage.FirstOrDefault(c => c.Event == BuildLifecycleEvent.Toggle);
        if (toggleCoverage is not null)
        {
            bool keyed = !launch.IsAlways
                || (acting ?? Array.Empty<PlanCondition>()).Any(condition => !condition.IsAlways);
            if (keyed && toggleCoverage.State == BuildCoverageState.NotApplicable)
                errors.Add("toggle lifecycle is not applicable despite a key acting on this part");
            if (!keyed && toggleCoverage.State != BuildCoverageState.NotApplicable)
                errors.Add("toggle lifecycle is active while no key acts on this part");
        }
        if (plan.InitialCondition is null) errors.Add("lifecycle plan has no launch condition");
        else if (plan.InitialCondition != launch)
            errors.Add($"lifecycle starts in {plan.InitialCondition}, expected {launch}");
        return errors;
    }

    private static void ValidatePasses(IReadOnlyList<BuildPassCoverage>? passes, string at,
        List<string> errors)
    {
        if (passes is null)
        {
            errors.Add($"{at} has no pass coverage");
            return;
        }
        foreach (var pass in Enum.GetValues<BuildRenderPass>())
        {
            var rows = passes.Where(p => p.Pass == pass).ToList();
            if (rows.Count != 1) errors.Add($"{at} accounts for {pass} {rows.Count} times");
        }
        foreach (var row in passes)
        {
            if (string.IsNullOrWhiteSpace(row.Reason)) errors.Add($"{at} {row.Pass} has no reason");
            if (!Enum.IsDefined(row.Pass)) errors.Add($"{at} has an unknown render pass");
            if (!Enum.IsDefined(row.State)) errors.Add($"{at} {row.Pass} has an unknown coverage state");
        }
    }

    private static void ValidateVisibility(BuildVisibilityDomain? visibility, string at,
        List<string> errors)
    {
        if (visibility is null)
        {
            errors.Add($"{at} has no visibility domain");
            return;
        }
        if (!Named(visibility.Scenes)) errors.Add($"{at} has no valid scene domain");
        if (!Named(visibility.OutfitStates)) errors.Add($"{at} has no valid outfit domain");
        if (!Named(visibility.Tiers)) errors.Add($"{at} has no valid tier domain");
        if (string.IsNullOrWhiteSpace(visibility.InstanceScope)) errors.Add($"{at} has no instance scope");
        if (string.IsNullOrWhiteSpace(visibility.Reason)) errors.Add($"{at} has no visibility reason");
    }

    private static void ValidateBounds(BuildCarrierBounds? bounds, string at, List<string> errors)
    {
        if (bounds is null)
        {
            errors.Add($"{at} has no bounds account");
            return;
        }
        if (!Enum.IsDefined(bounds.Basis)) errors.Add($"{at} has an invalid bounds basis");
        if (string.IsNullOrWhiteSpace(bounds.Reason)) errors.Add($"{at} has no bounds reason");
        if (bounds.Basis == BuildBoundsBasis.Unavailable)
        {
            if (bounds.Min is not null || bounds.Max is not null)
                errors.Add($"{at} carries bounds while declaring them unavailable");
            return;
        }
        if (bounds.Min is not { Count: 3 } || bounds.Max is not { Count: 3 })
        {
            errors.Add($"{at} bounds are not two 3D points");
            return;
        }
        for (int i = 0; i < 3; i++)
        {
            if (!float.IsFinite(bounds.Min[i]) || !float.IsFinite(bounds.Max[i]))
                errors.Add($"{at} bounds contain a non-finite value");
            else if (bounds.Min[i] > bounds.Max[i])
                errors.Add($"{at} bounds minimum exceeds its maximum");
        }
    }

    private static void ValidateMaterialValueFields(
        IReadOnlyList<BuildMaterialValueField>? fields, string at, List<string> errors)
    {
        if (fields is null) return;
        var semantics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Semantic))
                errors.Add($"{at} has a material-value field without a semantic");
            else if (!semantics.Add(field.Semantic))
                errors.Add($"{at} declares material-value field '{field.Semantic}' more than once");
            if (field.ConstantBufferSlot < 0)
                errors.Add($"{at} material-value field '{field.Semantic}' has an invalid buffer slot");
            if (field.ByteOffset < 0 || field.ByteOffset % sizeof(float) != 0)
                errors.Add($"{at} material-value field '{field.Semantic}' has an invalid byte offset");
            if (string.IsNullOrWhiteSpace(field.Proof))
                errors.Add($"{at} material-value field '{field.Semantic}' has no proof");
        }
    }

    private static bool Proof(BuildTargetingProof? proof) =>
        proof is not null && !string.IsNullOrWhiteSpace(proof.Kind)
        && !string.IsNullOrWhiteSpace(proof.Detail);

    private static bool SameProof(BuildTargetingProof? left, BuildTargetingProof? right) =>
        left is not null && right is not null
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Detail, right.Detail, StringComparison.Ordinal);

    private static bool Named(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } && values.All(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>An emission must carry the condition the plan gave it, term for term. The planner derives
    /// that condition from authored intent alone — the part's own group and state for the ordinal term, and
    /// the states of other groups whose also-hidden list actually names the part for the exceptions — so a
    /// backend that gates on a foreign key, an extra state or a hide nobody authored is refused here.</summary>
    private static void ValidateGate(BuildEmissionGate? gate, BuildEmissionGate expected, string at,
        List<string> errors)
    {
        if (gate is null)
        {
            errors.Add($"{at} has no gate for its authored condition");
            return;
        }
        if (gate.ActiveWhen is not { Count: > 0 })
        {
            errors.Add($"{at} names no condition under which it acts");
            return;
        }
        if (gate.UnlessAny is null)
        {
            errors.Add($"{at} has no suppression-exception account");
            return;
        }
        // A term that names a key group must name one. An empty group or key states no condition at all, and
        // a gate carrying one is refused here rather than compared, where an unnamed group could be read as
        // the keyless term that holds in every session.
        int before = errors.Count;
        foreach (var term in gate.ActiveWhen.Concat(gate.UnlessAny))
        {
            if (term is null) errors.Add($"{at} has a gate term with no condition");
            else if (term.IsAlways) continue;
            else if (string.IsNullOrWhiteSpace(term.GroupId))
                errors.Add($"{at} is gated by a term naming no key group");
            else if (string.IsNullOrWhiteSpace(term.Key))
                errors.Add($"{at} is gated by a term naming no key");
        }
        if (errors.Count != before) return;
        if (!gate.SameTermsAs(expected))
            errors.Add($"{at} is gated by '{gate}', expected '{expected}'");
    }
}
