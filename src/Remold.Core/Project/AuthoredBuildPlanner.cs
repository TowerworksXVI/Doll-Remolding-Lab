using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Remold.Core.Mesh;

namespace Remold.Core.Project;

public enum BuildPlanVerdict
{
    Resolved,
    InheritedAsRequested,
    Unsupported,
    Unresolved,
    NeedsRepair,
    Conflict,
}

public enum BuildRuntimeAction
{
    None,
    BindProjectAsset,
    BindGameSource,
    GenerateNeutral,
    Hide,
}

/// <summary>The two-state vocabulary the shipped runtime compiler speaks. The plan states conditions with
/// <see cref="PlanCondition"/> instead; this remains as the projection of a two-state group.</summary>
public enum BuildPlanState
{
    Active,
    ToggleOff,
}

/// <summary>When one planned operation applies: always, or the named key group standing in one named state.
/// The cycle length and launch position travel with the condition so a plan reader never has to re-read
/// authored intent to know how many positions the key has or where it starts.</summary>
public sealed record PlanCondition(
    string? GroupId,
    string? Key,
    int StateIndex,
    int StateCount,
    int StartState)
{
    /// <summary>The condition of a part no key can switch. It holds in every session.</summary>
    public static PlanCondition Always { get; } = new(null, null, 0, 1, 0);

    public bool IsAlways => GroupId is null;

    public BuildGateTerm Term => IsAlways
        ? BuildGateTerm.Always : new BuildGateTerm(GroupId, Key, StateIndex);

    public override string ToString() => IsAlways
        ? "always" : $"{Key} state {StateIndex} of {StateCount}";
}

public enum EffectiveValueKind
{
    TargetGameValue,
    ProjectAsset,
    SourceGameSlot,
    InheritedLiveCarrier,
    Neutral,
    Hidden,
}

public enum PlannedPartDisposition
{
    Edit,
    Vanilla,
    Hidden,
}

/// <summary>The runtime discriminator that keeps one emitted action on its intended draw.</summary>
public sealed record BuildTargetingProof(string Kind, string Detail);

/// <summary>A backend verdict for one effective request. Resolved runtime actions require a targeting
/// proof; inherited values emit no action.
///
/// <para><see cref="Reason"/> is shown; <see cref="Detail"/> is not. A line the planner writes for a
/// modder carries only a reason. A line about the planner's own consistency — a backend that answered with
/// something the seam forbids, an id claimed twice — states the plain reason and puts its account, which
/// names slots, rows and emitted ids, in the detail, where <see cref="AuthoredBuildPlan.Diagnostics"/>
/// gathers it for the log.</para></summary>
public sealed record BuildPlanDecision(
    BuildPlanVerdict Verdict,
    BuildRuntimeAction Action,
    BuildTargetingProof? TargetingProof,
    string Reason,
    string? Detail = null)
{
    public bool BlocksBuild => Verdict is BuildPlanVerdict.Unsupported or BuildPlanVerdict.Unresolved
        or BuildPlanVerdict.NeedsRepair or BuildPlanVerdict.Conflict;

    public static BuildPlanDecision Inherited(string reason) => new(
        BuildPlanVerdict.InheritedAsRequested, BuildRuntimeAction.None, null, reason);

    public static BuildPlanDecision Blocked(BuildPlanVerdict verdict, string reason) => new(
        verdict, BuildRuntimeAction.None, null, reason);

    /// <summary>…and the same with the account only the log reads beside it.</summary>
    public static BuildPlanDecision Blocked(BuildPlanVerdict verdict, string reason, string? detail) =>
        new(verdict, BuildRuntimeAction.None, null, reason, detail);
}

/// <summary>The current-install identity of an authored structural slot. <inheritdoc
/// cref="BuildPlanDecision" path="/summary/para"/></summary>
public sealed record BuildSlotResolution(
    BuildPlanVerdict Verdict,
    TargetSlot? CurrentSlot,
    string Reason,
    string? Detail = null);

/// <summary>The terminal value reached after following a binding's source-slot chain.</summary>
public sealed record EffectiveBuildValue(
    EffectiveValueKind Kind,
    ProjectAsset? ProjectAsset,
    TargetSlot? SourceGameSlot,
    IReadOnlyList<string> SourceChain);

public sealed record BuildBindingRequest(
    string RowId,
    string EditDefinitionId,
    TargetSlot AuthoredSlot,
    TargetSlot CurrentSlot,
    Binding RequestedBinding,
    EffectiveBuildValue EffectiveValue,
    BuildEmissionGate Gate);

/// <summary>The install/backend seam. Slot resolution re-anchors authored structure before capability is
/// judged; capability then supplies the runtime action and its targeting proof.</summary>
public interface IAuthoredBuildBackend
{
    BuildSlotResolution ResolveSlot(TargetSlot authoredSlot);
    BuildOperationResolution ResolveBinding(BuildBindingRequest request);
    BuildOperationResolution ResolveVisibility(BuildVisibilityRequest request);
    BuildLifecycleResolution ResolveLifecycle(BuildLifecycleRequest request);
}

public sealed record PlannedBinding(
    string RowId,
    string EditDefinitionId,
    TargetSlot AuthoredSlot,
    TargetSlot? CurrentSlot,
    Binding RequestedBinding,
    EffectiveBuildValue? EffectiveValue,
    BuildEmissionGate Gate,
    BuildOperationResolution Operation)
{
    public BuildPlanDecision Decision => Operation.Decision;
    public BuildRenderPlan? RenderPlan => Operation.RenderPlan;
    public IReadOnlyList<BuildRuntimeEmission> Emissions =>
        Operation.Emissions ?? Array.Empty<BuildRuntimeEmission>();
    public IReadOnlyList<BuildOutputArtifact> OutputArtifacts =>
        Operation.OutputArtifacts ?? Array.Empty<BuildOutputArtifact>();
}

/// <summary>One active edit in the derived per-part account. <see cref="ActiveWhen"/> is the complete OR-list
/// of placements under which it resolves; <see cref="Condition"/> is its first placement for compatibility
/// consumers that only display one.</summary>
public sealed record PlannedPartOperation(
    PlanCondition Condition,
    PlannedPartDisposition Disposition,
    string? EditDefinitionId,
    BuildOperationResolution? Operation,
    IReadOnlyList<PlannedBinding> Bindings,
    IReadOnlyList<PlanCondition> ActiveWhen);

/// <summary>One key group with at least one placement targeting this part.</summary>
public sealed record PlannedGroupTouch(string GroupId, string? Key, int StateCount,
    IReadOnlyList<PlanCondition> Conditions);

public sealed record PlannedPart(
    TargetPart Target,
    PlannedPartDisposition Disposition,
    string? EditDefinitionId,
    PartToggle? Toggle,
    IReadOnlyList<PlannedPartOperation> Operations,
    IReadOnlyList<PlanCondition> HideConditions,
    BuildOperationResolution? Suppression,
    BuildLifecycleResolution? Lifecycle,
    IReadOnlyList<PlannedBinding> Bindings,
    IReadOnlyList<PlannedGroupTouch> GroupTouches)
{
    /// <summary>The two-state view the shipped runtime compiler still reads: what state 0 answers, and what
    /// state 1 answers when the part's group has exactly two states. A longer cycle has no answer here, and
    /// the layers that consume it refuse such a plan by name rather than reading state 0 as the whole
    /// truth.</summary>
    public BuildOperationResolution? ActiveOperation => StateOperation(Toggle?.StartsOff == true ? 1 : 0)?.Operation;
    public BuildOperationResolution? ToggleOffOperation =>
        Toggle is null ? null : StateOperation(Toggle.StartsOff ? 0 : 1)?.Operation;

    public BuildPlanDecision? ActiveDecision => ActiveOperation?.Decision;
    public BuildRenderPlan? ActiveRenderPlan => ActiveOperation?.RenderPlan;
    public BuildPlanDecision? ToggleOffDecision => ToggleOffOperation?.Decision;
    public BuildRenderPlan? ToggleOffRenderPlan => ToggleOffOperation?.RenderPlan;

    private PlannedPartOperation? StateOperation(int index) => Operations.FirstOrDefault(operation =>
        operation.Condition.IsAlways ? index == 0 : operation.Condition.StateIndex == index);
}

public sealed record PlannedRuntimeEmission(
    string Consumer,
    BuildPlanVerdict Verdict,
    BuildRuntimeEmission Emission);

public sealed record PlannedOutputArtifact(
    string Consumer,
    BuildPlanVerdict Verdict,
    BuildOutputArtifact Artifact);

/// <summary>Reverse view of a project-owned input. Required means an active binding consumes it; it does
/// not claim that the source file ships verbatim.</summary>
public sealed record PlannedProjectArtifact(
    string ProjectAssetId,
    string File,
    bool RequestedByActivePlan,
    bool RequiredByActivePlan,
    bool? Available,
    IReadOnlyList<string> Consumers,
    IReadOnlyList<string> BlockedConsumers,
    string Reason);

/// <summary>Project-asset identity carried into downstream metadata without claiming that the original
/// workspace file is an emitted artifact.</summary>
public sealed record PlannedIntentAsset(
    string Id,
    ProjectAssetKind Kind,
    string Label,
    ProjectAssetSource? Source,
    ProjectAssetValue? Value);

public sealed class AuthoredBuildPlan
{
    public IReadOnlyList<PlannedPart> Parts { get; init; } = Array.Empty<PlannedPart>();
    public IReadOnlyList<PlannedBinding> Bindings { get; init; } = Array.Empty<PlannedBinding>();
    public IReadOnlyList<PlannedProjectArtifact> ProjectArtifacts { get; init; }
        = Array.Empty<PlannedProjectArtifact>();
    public IReadOnlyList<PlannedIntentAsset> IntentAssets { get; init; }
        = Array.Empty<PlannedIntentAsset>();
    public IReadOnlyList<PlannedRuntimeEmission> RuntimeEmissions { get; init; }
        = Array.Empty<PlannedRuntimeEmission>();
    public IReadOnlyList<PlannedOutputArtifact> OutputArtifacts { get; init; }
        = Array.Empty<PlannedOutputArtifact>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The technical half of every line above that states its plain half on screen: the seam that
    /// disagreed, the slot or row it disagreed about, the emitted id claimed twice. Nothing here is shown —
    /// it is written to the log, so a blocked build the modder cannot act on is still one somebody can
    /// read.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    /// <summary>Which edits each conflict or warning line above is about, keyed by the line itself. A page
    /// marks its rows from this rather than from what an edit's name happens to appear in. A line the
    /// planner cannot attribute is absent, and marks nothing.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> IssueEditIds { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Which key groups a line is about. Group ownership stays structured so an empty keyless group
    /// still has a board target and duplicate display names never have to be recovered from the sentence.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> IssueGroupIds { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public bool CanBuild => Conflicts.Count == 0
        && Parts.All(p => p.Operations.All(o => !Blocks(o.Operation?.Decision)
                && o.Operation?.RenderPlan?.BlocksBuild != true)
            && !Blocks(p.Suppression?.Decision) && p.Suppression?.RenderPlan?.BlocksBuild != true
            && p.Lifecycle?.BlocksBuild != true)
        && Bindings.All(b => !b.Decision.BlocksBuild && b.RenderPlan?.BlocksBuild != true);

    private static bool Blocks(BuildPlanDecision? decision) => decision?.BlocksBuild == true;
}

/// <summary>Pure authored-intent planning. The project is validated, active composition is selected,
/// source lineage is resolved, and the backend is asked only after current-install slot identity exists.</summary>
public static class AuthoredBuildPlanner
{
    /// <summary>What a plan line says when what went wrong is the planning itself: a backend that answered
    /// with something the seam forbids, two emitted things claiming one identity. There is nothing in it a
    /// modder made and nothing they can change, so the line says only that, and its account — the seam, the
    /// slot, the id — goes to the log through <see cref="AuthoredBuildPlan.Diagnostics"/>. Written as a
    /// clause because the page's own attribution puts "Cannot build ⟨edit⟩ on ⟨part⟩:" in front of it.</summary>
    public const string InternalGuard = "Doll Remolding Lab couldn't work out how to build this";

    public static AuthoredBuildPlan Plan(AuthoredProject project, IAuthoredBuildBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(backend);
        cancellationToken.ThrowIfCancellationRequested();
        var errors = AuthoredProjectValidator.Errors(project);
        cancellationToken.ThrowIfCancellationRequested();
        if (errors.Count > 0)
            throw new InvalidDataException("authored project is invalid: " + string.Join("; ", errors));

        var assets = project.ProjectAssets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var slots = project.TargetSlots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var edits = project.EditDefinitions.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var consumers = assets.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var blockedConsumers = assets.Keys.ToDictionary(id => id, _ => new List<string>(),
            StringComparer.Ordinal);
        var parts = new List<PlannedPart>();
        var allBindings = new List<PlannedBinding>();
        var warnings = AuthoredProjectValidator.Warnings(project).ToList();
        var owners = new IssueOwners();
        var initialConflicts = new List<string>();
        // A group with no key blocks every edit used inside it, so those are the rows the line marks and
        // the placements its chips jump to.
        foreach (var group in project.KeyGroups.Where(group => group.Key is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string conflict = KeylessGroupConflict(group);
            initialConflicts.Add(conflict);
            owners.OwnGroup(conflict, group.Id);
            owners.Own(conflict, group.States.SelectMany(state => state.ActiveEditIds)
                .Distinct(StringComparer.Ordinal).ToArray());
        }
        var activations = Activations(project, edits, warnings, owners);
        warnings.AddRange(DeadContentWarnings(activations, owners));

        // The per-part account is derived from edit activations. One edit resolves once under the OR of all
        // its placements; several edits and groups may therefore contribute operations to the same part.
        foreach (var partActivations in activations.GroupBy(activation => activation.Edit.Target.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = partActivations.ToList();
            var target = ordered[0].Edit.Target;
            var content = ordered.Where(activation => activation.Edit.Kind == EditDefinitionKind.Content)
                .ToList();
            var hides = ordered.Where(activation => activation.Edit.Kind == EditDefinitionKind.Hide).ToList();
            var hideConditions = hides.SelectMany(activation => activation.Placements)
                .Select(placement => placement.Condition).Distinct().ToArray();
            var hideTerms = hideConditions.Select(condition => condition.Term).ToArray();
            var suppression = hideTerms.Length == 0 ? null
                : PlanVisibility(backend, target,
                    new BuildEmissionGate(hideTerms, Array.Empty<BuildGateTerm>()), slots);

            var partBindings = new List<PlannedBinding>();
            var operations = new List<PlannedPartOperation>();
            foreach (var activation in content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conditions = activation.Placements.Select(placement => placement.Condition).ToArray();
                var gate = new BuildEmissionGate(conditions.Select(condition => condition.Term).ToArray(),
                    hideTerms);
                var rows = new List<PlannedBinding>();
                foreach (var binding in activation.Edit.Bindings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = PlanBinding(project, backend, activation.Edit, binding, gate,
                        assets, slots, edits);
                    rows.Add(row);
                }
                rows = DropCarrierlessMaterialValues(project, activation.Edit, rows, warnings, owners);
                rows = DropGamePicturesOnReplacement(activation.Edit, rows, warnings, owners);
                foreach (var row in rows)
                {
                    partBindings.Add(row);
                    allBindings.Add(row);
                    if (row.EffectiveValue?.ProjectAsset is { } asset)
                    {
                        if (row.Decision.BlocksBuild) blockedConsumers[asset.Id].Add(row.RowId);
                        else if (row.Decision.Action != BuildRuntimeAction.None)
                            consumers[asset.Id].Add(row.RowId);
                    }
                }
                operations.Add(new PlannedPartOperation(conditions[0], PlannedPartDisposition.Edit,
                    activation.Edit.Id, null, rows, conditions));
            }
            foreach (var activation in hides)
            {
                var conditions = activation.Placements.Select(placement => placement.Condition).ToArray();
                operations.Add(new PlannedPartOperation(conditions[0], PlannedPartDisposition.Hidden,
                    activation.Edit.Id, suppression, Array.Empty<PlannedBinding>(), conditions));
            }

            var groupTouches = ordered.SelectMany(activation => activation.Placements)
                .Where(placement => !placement.Condition.IsAlways)
                .GroupBy(placement => placement.Condition.GroupId!, StringComparer.Ordinal)
                .Select(group => new PlannedGroupTouch(group.Key, group.First().Condition.Key,
                    group.First().Condition.StateCount,
                    group.Select(placement => placement.Condition).Distinct().ToArray())).ToArray();
            // This is the lifecycle-request condition derived from the first touching group, not the part's
            // complete activation account.
            var launch = groupTouches.FirstOrDefault() is { } firstGroup
                ? new PlanCondition(firstGroup.GroupId, firstGroup.Key, 0, firstGroup.StateCount, 0)
                : PlanCondition.Always;
            var acting = operations.Where(operation => HasRuntimeAction(operation.Operation?.Decision)
                    || operation.Bindings.Any(binding => HasRuntimeAction(binding.Decision)))
                .SelectMany(operation => operation.ActiveWhen).ToList();
            if (HasRuntimeAction(suppression?.Decision)) acting.AddRange(hideConditions);
            acting = acting.Distinct().ToList();
            var disposition = content.Count > 0 ? PlannedPartDisposition.Edit
                : PlannedPartDisposition.Hidden;
            var lifecycle = acting.Count > 0
                ? NormalizeLifecycle(backend.ResolveLifecycle(new BuildLifecycleRequest(target,
                    disposition, launch, acting)), target, launch, acting)
                : null;
            parts.Add(new PlannedPart(target, disposition,
                content.FirstOrDefault()?.Edit.Id ?? hides.First().Edit.Id,
                TwoStateToggle(groupTouches, content, hides), operations, hideConditions,
                suppression, lifecycle, partBindings, groupTouches));
        }

        var artifacts = project.ProjectAssets.Select(asset =>
        {
            var used = consumers[asset.Id];
            var blocked = blockedConsumers[asset.Id];
            bool? available = project.RootDir is null
                ? null
                : File.Exists(Path.Combine(project.RootDir, asset.File.Replace('/', Path.DirectorySeparatorChar)));
            string reason = used.Count > 0
                ? $"required by {used.Count} resolved active binding{(used.Count == 1 ? "" : "s")}" +
                    (blocked.Count == 0 ? "" : $"; also requested by {blocked.Count} blocked binding" +
                        (blocked.Count == 1 ? "" : "s"))
                : blocked.Count > 0
                    ? $"requested by {blocked.Count} blocked active binding" +
                        (blocked.Count == 1 ? "" : "s")
                    : "not used by any active edit";
            return new PlannedProjectArtifact(asset.Id, asset.File, used.Count + blocked.Count > 0,
                used.Count > 0, available, used.ToArray(), blocked.ToArray(), reason);
        }).ToArray();

        var runtimeEmissions = new List<PlannedRuntimeEmission>();
        var outputArtifacts = new List<PlannedOutputArtifact>();
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(part.Suppression, part.Target.Key + ":hide");
            foreach (var binding in part.Bindings) Add(binding.Operation, binding.RowId);
        }

        var diagnostics = new List<string>();
        initialConflicts.AddRange(ContentActivationConflicts(activations, owners));
        initialConflicts.AddRange(PlanConflicts(parts, runtimeEmissions, outputArtifacts, owners,
            diagnostics));
        // Read off the finished rows rather than gathered as they were written: a detail rides its own
        // decision, so nothing has to be threaded down through the resolution recursion to reach here.
        diagnostics.AddRange(allBindings.Select(binding => binding.Decision.Detail)
            .Concat(parts.SelectMany(part => part.Operations
                .Select(operation => operation.Operation?.Decision.Detail)
                .Append(part.Suppression?.Decision.Detail)
                .Append(part.Lifecycle?.Detail)))
            .Where(detail => !string.IsNullOrWhiteSpace(detail)).Select(detail => detail!));
        var conflicts = initialConflicts.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new AuthoredBuildPlan
        {
            Diagnostics = diagnostics.Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Parts = parts,
            Bindings = allBindings,
            ProjectArtifacts = artifacts,
            IntentAssets = project.ProjectAssets.Select(asset => new PlannedIntentAsset(asset.Id,
                asset.Kind, asset.Label, asset.Source, asset.Value)).ToArray(),
            RuntimeEmissions = runtimeEmissions,
            OutputArtifacts = outputArtifacts,
            Conflicts = conflicts,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            IssueEditIds = owners.Read(),
            IssueGroupIds = owners.ReadGroups(),
        };

        void Add(BuildOperationResolution? operation, string consumer)
        {
            if (operation?.Emissions is { } emissions)
                runtimeEmissions.AddRange(emissions.Select(e => new PlannedRuntimeEmission(consumer,
                    operation.Decision.Verdict, e)));
            if (operation?.OutputArtifacts is { } outputs)
                outputArtifacts.AddRange(outputs.Select(a => new PlannedOutputArtifact(consumer,
                    operation.Decision.Verdict, a)));
        }
    }

    /// <summary>Material patches ride a replacement's own submitted geometry. Remove an otherwise valid
    /// patch when this edit has no replacement in the build, or when the replacement file proves that the
    /// addressed material position contains no indices. This is authored residue, not a capability failure:
    /// the rest of the edit remains buildable and the warning explains the omitted value.</summary>
    /// <summary>The edits each conflict or warning line is about, gathered as the lines are written. The
    /// alternative a page is left with otherwise is matching an edit's name against the text, which marks
    /// every edit that shares a name and every edit whose name is a word.</summary>
    private sealed class IssueOwners
    {
        private readonly Dictionary<string, List<string>> _byLine = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _groupsByLine = new(StringComparer.Ordinal);

        internal void Own(string line, params string[] editDefinitionIds)
        {
            if (!_byLine.TryGetValue(line, out var ids)) _byLine.Add(line, ids = new List<string>());
            foreach (string id in editDefinitionIds)
                if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        }

        internal void OwnGroup(string line, string groupId)
        {
            if (!_groupsByLine.TryGetValue(line, out var ids))
                _groupsByLine.Add(line, ids = new List<string>());
            if (!ids.Contains(groupId, StringComparer.Ordinal)) ids.Add(groupId);
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Read() =>
            _byLine.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.Ordinal);

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> ReadGroups() =>
            _groupsByLine.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.Ordinal);
    }

    private static List<PlannedBinding> DropCarrierlessMaterialValues(AuthoredProject project,
        EditDefinition edit, List<PlannedBinding> rows, List<string> warnings, IssueOwners owners)
    {
        var values = rows.Where(row => row.AuthoredSlot.Input == TargetInputKind.MaterialValue
            && row.Decision.Verdict == BuildPlanVerdict.Resolved
            && row.Emissions.Any(emission => emission.Kind == BuildEmissionKind.MaterialValuePatch))
            .ToList();
        if (values.Count == 0) return rows;

        var replacement = rows.FirstOrDefault(row => row.AuthoredSlot.Input == TargetInputKind.Geometry
            && (row.AuthoredSlot.Tier is null || string.Equals(row.AuthoredSlot.Tier, "lod0",
                StringComparison.OrdinalIgnoreCase))
            && !row.Decision.BlocksBuild
            && row.EffectiveValue?.ProjectAsset is { Kind: ProjectAssetKind.Geometry });
        if (replacement?.EffectiveValue?.ProjectAsset is not { } geometry)
        {
            string warning = $"{EditName(edit)}'s shading values will not take effect: they apply only "
                + "through this edit's own mesh replacement, and it has none in this build. Replace the "
                + "part's mesh in this edit, or remove its shading values.";
            warnings.Add(warning);
            owners.Own(warning, edit.Id);
            return rows.Select(row => values.Contains(row) ? Drop(row, warning) : row).ToList();
        }

        int[]? counts = ReplacementIndexCounts(project, geometry);
        var dropped = new Dictionary<PlannedBinding, string>();
        foreach (var value in values)
        {
            int position = value.CurrentSlot?.MaterialSlotIndex
                ?? value.CurrentSlot?.SubmeshIndex
                ?? value.AuthoredSlot.MaterialSlotIndex
                ?? value.AuthoredSlot.SubmeshIndex
                ?? -1;
            if (position >= 0 && (value.CurrentSlot?.DrawIndexCount == 0
                || counts is not null && position >= counts.Length))
            {
                string warning = $"{EditName(edit)}'s shading value for material {position} will not "
                    + $"take effect: no faces in its replacement mesh use material {position}. Assign "
                    + "faces to that material, or remove the value.";
                warnings.Add(warning);
                owners.Own(warning, edit.Id);
                dropped[value] = warning;
            }
        }
        return dropped.Count == 0 ? rows
            : rows.Select(row => dropped.TryGetValue(row, out string? warning)
                ? Drop(row, warning) : row).ToList();

        static PlannedBinding Drop(PlannedBinding row, string reason) => row with
        {
            Operation = new BuildOperationResolution(BuildPlanDecision.Inherited(reason), null,
                Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>()),
        };
    }

    private static int[]? ReplacementIndexCounts(AuthoredProject project, ProjectAsset geometry)
    {
        if (project.RootDir is null) return null;
        string file = Path.Combine(project.RootDir,
            geometry.File.Replace('/', Path.DirectorySeparatorChar));
        try { return MeshGltf.ImportGlb(file).Submeshes.Select(indices => indices.Length).ToArray(); }
        catch { return null; }
    }

    /// <summary>A replacement draws only its edit-output maps. Old development files may still bind
    /// stock game-material pictures on the same edit; keep that residue advisory and omit its emissions.</summary>
    private static List<PlannedBinding> DropGamePicturesOnReplacement(EditDefinition edit,
        List<PlannedBinding> rows, List<string> warnings, IssueOwners owners)
    {
        bool hasReplacement = rows.Any(row => row.AuthoredSlot.Input == TargetInputKind.Geometry
            && (row.AuthoredSlot.Tier is null || string.Equals(row.AuthoredSlot.Tier, "lod0",
                StringComparison.OrdinalIgnoreCase))
            && !row.Decision.BlocksBuild
            && row.EffectiveValue?.ProjectAsset is { Kind: ProjectAssetKind.Geometry });
        if (!hasReplacement) return rows;

        var pictures = rows.Where(row => row.AuthoredSlot.Domain == TargetSlotDomain.Game
                && row.AuthoredSlot.Input is TargetInputKind.BaseColor
                    or TargetInputKind.Normal or TargetInputKind.Rmo or TargetInputKind.Blend
                    or TargetInputKind.Texture
                && row.RequestedBinding.Kind != BindingKind.TargetGameValue
                && row.Decision.Verdict == BuildPlanVerdict.Resolved)
            .ToHashSet();
        if (pictures.Count == 0) return rows;

        string warning = $"{EditName(edit)} replaces the part's mesh, so its changes to the original "
            + "textures will not take effect. A replacement uses this edit's own maps instead.";
        warnings.Add(warning);
        owners.Own(warning, edit.Id);
        return rows.Select(row => pictures.Contains(row) ? row with
        {
            Operation = new BuildOperationResolution(BuildPlanDecision.Inherited(warning), null,
                Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>()),
        } : row).ToList();
    }

    private sealed record AuthoredPlacement(PlanCondition Condition, string Name);
    private sealed record EditActivation(EditDefinition Edit, IReadOnlyList<AuthoredPlacement> Placements)
    {
        internal BuildEmissionGate Gate => new(Placements.Select(placement => placement.Condition.Term).ToArray(),
            Array.Empty<BuildGateTerm>());
    }

    private static IReadOnlyList<EditActivation> Activations(AuthoredProject project,
        IReadOnlyDictionary<string, EditDefinition> edits, List<string> warnings, IssueOwners owners)
    {
        var order = new List<string>();
        var placements = new Dictionary<string, List<AuthoredPlacement>>(StringComparer.Ordinal);
        void Add(string editId, AuthoredPlacement placement)
        {
            if (!placements.TryGetValue(editId, out var list))
            {
                placements.Add(editId, list = new List<AuthoredPlacement>());
                order.Add(editId);
            }
            list.Add(placement);
        }

        foreach (string editId in project.Always)
            Add(editId, new AuthoredPlacement(PlanCondition.Always, PlacementNames.Always));
        foreach (var group in project.KeyGroups.Where(group => group.Key is not null))
            for (int i = 0; i < group.States.Count; i++)
                foreach (string editId in group.States[i].ActiveEditIds)
                    Add(editId, new AuthoredPlacement(
                        new PlanCondition(group.Id, group.Key, i, group.States.Count, 0),
                        PlacementNames.Place(group, group.States[i], i)));

        // The part and the consequence, not just the name: two parts can carry edits named alike, and
        // "redundant" leaves the modder to work out what the mod will actually do.
        foreach (var pair in placements.Where(pair => pair.Value.Any(placement => placement.Condition.IsAlways)))
            foreach (var redundant in pair.Value.Where(placement => !placement.Condition.IsAlways))
            {
                string warning = $"{EditName(edits[pair.Key])} on {PartName(edits[pair.Key].Target)} is "
                    + $"in Always, so using it in {redundant.Name} changes nothing. Remove it from Always "
                    + "for the key to switch it.";
                warnings.Add(warning);
                owners.Own(warning, pair.Key);
            }
        return order.Select(id => new EditActivation(edits[id], placements[id])).ToArray();
    }

    private static IReadOnlyList<string> DeadContentWarnings(IReadOnlyList<EditActivation> activations,
        IssueOwners owners)
    {
        var warnings = new List<string>();
        foreach (var part in activations.GroupBy(activation => activation.Edit.Target.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            var hides = part.Where(activation => activation.Edit.Kind == EditDefinitionKind.Hide)
                .SelectMany(activation => activation.Placements).ToArray();
            foreach (var content in part.Where(activation => activation.Edit.Kind == EditDefinitionKind.Content))
            {
                foreach (var placement in content.Placements)
                {
                    if (!HiddenAt(placement, hides)) continue;
                    string partName = PartName(content.Edit.Target);
                    string warning = $"{EditName(content.Edit)} on {partName} never appears in "
                        + $"{placement.Name} because {partName} is hidden there.";
                    warnings.Add(warning);
                    owners.Own(warning, content.Edit.Id);
                }
            }
        }
        return warnings;

        static bool HiddenAt(AuthoredPlacement placement, IReadOnlyList<AuthoredPlacement> hides) =>
            hides.Any(hide => hide.Condition.IsAlways
                || !placement.Condition.IsAlways
                && string.Equals(hide.Condition.GroupId, placement.Condition.GroupId, StringComparison.Ordinal)
                && hide.Condition.StateIndex == placement.Condition.StateIndex);
    }

    private static IReadOnlyList<string> ContentActivationConflicts(
        IReadOnlyList<EditActivation> activations, IssueOwners owners)
    {
        var conflicts = new List<string>();
        foreach (var part in activations.Where(activation => activation.Edit.Kind == EditDefinitionKind.Content)
                     .GroupBy(activation => activation.Edit.Target.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = part.ToList();
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                {
                    if (rows[i].Gate.ProvablyExclusiveOf(rows[j].Gate)) continue;
                    string conflict = $"{EditName(rows[i].Edit)} at {Places(rows[i])} and "
                        + $"{EditName(rows[j].Edit)} at {Places(rows[j])} can be active together on "
                        + $"{PartName(rows[i].Edit.Target)}.";
                    conflicts.Add(conflict);
                    owners.Own(conflict, rows[i].Edit.Id, rows[j].Edit.Id);
                }
        }
        return conflicts;

        static string Places(EditActivation activation) =>
            string.Join(" or ", activation.Placements.Select(placement => placement.Name));
    }

    private static string KeylessGroupConflict(KeyGroup group) =>
        $"{KeyGroupSubject(group)} has no key. This blocks the build. Give it a key, or delete the group.";

    private static string KeyGroupSubject(KeyGroup group) =>
        !string.IsNullOrWhiteSpace(group.Label) ? $"Key group '{group.Label.Trim()}'"
        : !string.IsNullOrWhiteSpace(group.Key) ? $"Key group {group.Key}"
        : PlacementNames.UnnamedGroup;

    private static string EditName(EditDefinition edit) =>
        !string.IsNullOrWhiteSpace(edit.Label) ? edit.Label.Trim()
        : edit.Kind == EditDefinitionKind.Hide ? "the hide edit" : "an unnamed edit";

    /// <summary>What a plan line calls a part: the short name the rest of the app shows it under, which is
    /// the renderer slot with the subject prefix and detail-level segment taken off. A slot name that does
    /// not carry either is already short and is used whole rather than cut down by guess.</summary>
    internal static string PartName(TargetPart part)
    {
        var segments = part.RendererSlot.Split('_');
        int start = segments.Length >= 3 && segments[0].Length == 1 ? 2 : 0;
        var shortSegments = segments[start..].Where(segment => !Tier(segment)).ToArray();
        return start == 0 && shortSegments.Length == segments.Length
            ? part.RendererSlot : string.Join('_', shortSegments).ToLowerInvariant();

        static bool Tier(string segment) => segment.Length > 3
            && segment.StartsWith("lod", StringComparison.OrdinalIgnoreCase)
            && segment.Skip(3).All(char.IsDigit);
    }

    private static PartToggle? TwoStateToggle(IReadOnlyList<PlannedGroupTouch> groups,
        IReadOnlyList<EditActivation> content, IReadOnlyList<EditActivation> hides)
    {
        if (groups is not { Count: 1 } || groups[0].Key is not { } key || groups[0].StateCount != 2)
            return null;
        string groupId = groups[0].GroupId;
        bool contentOn = content.Any(activation => activation.Placements.Any(placement =>
            string.Equals(placement.Condition.GroupId, groupId, StringComparison.Ordinal)
            && placement.Condition.StateIndex == 0));
        bool contentOff = content.Any(activation => activation.Placements.Any(placement =>
            string.Equals(placement.Condition.GroupId, groupId, StringComparison.Ordinal)
            && placement.Condition.StateIndex == 1));
        if (contentOn == contentOff) return null;
        int offIndex = contentOn ? 1 : 0;
        bool hiddenOff = hides.Any(activation => activation.Placements.Any(placement =>
            string.Equals(placement.Condition.GroupId, groupId, StringComparison.Ordinal)
            && placement.Condition.StateIndex == offIndex));
        return new PartToggle { Key = key, StartsOff = !contentOn,
            OffState = hiddenOff ? CompositionState.Hidden : CompositionState.Vanilla };
    }

    private static PlannedBinding PlanBinding(AuthoredProject project, IAuthoredBuildBackend backend,
        EditDefinition edit, Binding binding, BuildEmissionGate gate,
        IReadOnlyDictionary<string, ProjectAsset> assets,
        IReadOnlyDictionary<string, TargetSlot> slots, IReadOnlyDictionary<string, EditDefinition> edits)
    {
        var authoredSlot = slots[binding.SlotId];
        string rowId = edit.Id + ":" + binding.SlotId;
        var current = NormalizeSlot(backend.ResolveSlot(authoredSlot), authoredSlot, rowId);
        if (current.Verdict != BuildPlanVerdict.Resolved)
            return new PlannedBinding(rowId, edit.Id, authoredSlot, current.CurrentSlot, binding, null,
                gate, new BuildOperationResolution(
                    BuildPlanDecision.Blocked(current.Verdict, current.Reason, current.Detail), null));

        var effective = ResolveEffective(binding, edit.Id, backend, assets, slots, edits,
            new HashSet<string>(StringComparer.Ordinal), new List<string>());
        if (effective.Decision is { } sourceFailure)
            return new PlannedBinding(rowId, edit.Id, authoredSlot, current.CurrentSlot, binding,
                effective.Value, gate, new BuildOperationResolution(sourceFailure, null));

        var value = effective.Value!;
        if (binding.Kind == BindingKind.SourceSlot
            && value.Kind == EffectiveValueKind.InheritedLiveCarrier)
        {
            return new PlannedBinding(rowId, edit.Id, authoredSlot, current.CurrentSlot, binding, value,
                gate, new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported,
                    "the slot this value is copied from keeps the original, so there is nothing to copy"),
                    null));
        }
        if (value.ProjectAsset is { } asset && project.RootDir is null)
        {
            return new PlannedBinding(rowId, edit.Id, authoredSlot, current.CurrentSlot, binding, value,
                gate, new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Unresolved,
                    $"'{asset.Label}' cannot be found until the mod is saved. Save the mod, then build"),
                    null));
        }
        if (value.ProjectAsset is { } rootedAsset && project.RootDir is { } root
            && !File.Exists(Path.Combine(root,
                rootedAsset.File.Replace('/', Path.DirectorySeparatorChar))))
        {
            return new PlannedBinding(rowId, edit.Id, authoredSlot, current.CurrentSlot, binding, value,
                gate, new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Unresolved,
                    $"'{rootedAsset.Label}' is missing from the mod folder: {rootedAsset.File}"), null));
        }

        BuildOperationResolution resolution;
        if (value.Kind == EffectiveValueKind.TargetGameValue)
            resolution = new BuildOperationResolution(
                BuildPlanDecision.Inherited("vanilla"), null);
        else if (value.Kind == EffectiveValueKind.InheritedLiveCarrier)
            resolution = new BuildOperationResolution(BuildPlanDecision.Inherited("vanilla"), null);
        else if (value.Kind == EffectiveValueKind.Hidden)
        {
            // A hide takes no suppression exception: another group's hide asking for the same thing cannot
            // disagree with it, and letting that hide switch this one off would un-hide the part.
            gate = new BuildEmissionGate(gate.ActiveWhen, Array.Empty<BuildGateTerm>());
            resolution = NormalizeOperation(backend.ResolveVisibility(new BuildVisibilityRequest(edit.Target,
                    rowId + ":hide", authoredSlot, current.CurrentSlot!, gate)),
                BuildRuntimeAction.Hide, authoredSlot.Input, gate, rowId);
        }
        else
        {
            var request = new BuildBindingRequest(rowId, edit.Id, authoredSlot, current.CurrentSlot!,
                binding, value, gate);
            resolution = NormalizeOperation(backend.ResolveBinding(request), ExpectedAction(value.Kind),
                authoredSlot.Input, gate, rowId);
        }

        return new PlannedBinding(rowId, edit.Id, authoredSlot, current.CurrentSlot, binding, value,
            gate, resolution);
    }

    private static BuildOperationResolution PlanVisibility(IAuthoredBuildBackend backend, TargetPart target,
        BuildEmissionGate gate, IReadOnlyDictionary<string, TargetSlot> slots)
    {
        // A hide addresses the installed game object, so only the game domain can provide its anchor.
        var candidates = slots.Values.Where(s => s.Domain == TargetSlotDomain.Game
            && s.Input == TargetInputKind.Visibility && s.Part.SameAs(target)).ToList();
        var routes = new List<TargetSlot>();
        foreach (var candidate in candidates.OrderBy(slot => slot.Id, StringComparer.Ordinal))
            if (!routes.Any(route => route.SameRoute(candidate))) routes.Add(candidate);
        candidates = routes;
        string at = "hide of " + target.RendererSlot;
        if (candidates.Count == 0)
            candidates = slots.Values.Where(s => s.Domain == TargetSlotDomain.Game
                && s.Input == TargetInputKind.Geometry && s.Part.SameAs(target)
                && (s.Tier is null || string.Equals(s.Tier, "lod0", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.SubmeshIndex ?? int.MinValue)
                .ThenBy(s => s.MaterialSlotIndex ?? int.MinValue)
                .ThenBy(s => s.Id, StringComparer.Ordinal).Take(1).ToList();
        if (candidates.Count == 0)
            return new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Unresolved,
                "the mod records no mesh for this part, so it cannot be hidden"), null);
        if (candidates.Count > 1)
            return new BuildOperationResolution(BuildPlanDecision.Blocked(BuildPlanVerdict.Conflict,
                $"the mod records {candidates.Count} ways to hide this part, so the build cannot "
                + "choose one"), null);

        var authored = candidates[0];
        var current = NormalizeSlot(backend.ResolveSlot(authored), authored, at);
        if (current.Verdict != BuildPlanVerdict.Resolved)
            return new BuildOperationResolution(
                BuildPlanDecision.Blocked(current.Verdict, current.Reason, current.Detail), null);
        return NormalizeOperation(backend.ResolveVisibility(new BuildVisibilityRequest(target,
            target.Key + ":hide", authored, current.CurrentSlot!, gate)), BuildRuntimeAction.Hide,
            TargetInputKind.Visibility, gate, at);
    }

    private static (EffectiveBuildValue? Value, BuildPlanDecision? Decision) ResolveEffective(
        Binding binding, string editId, IAuthoredBuildBackend backend,
        IReadOnlyDictionary<string, ProjectAsset> assets, IReadOnlyDictionary<string, TargetSlot> slots,
        IReadOnlyDictionary<string, EditDefinition> edits, HashSet<string> visiting, List<string> chain)
    {
        string node = editId + ":" + binding.SlotId;
        if (!visiting.Add(node))
            return (null, DecisionGuard("source-slot bindings form a cycle at " + node));
        chain.Add(node);
        try
        {
            return binding.Kind switch
            {
                BindingKind.TargetGameValue => (Value(EffectiveValueKind.TargetGameValue), null),
                BindingKind.ProjectAsset => (new EffectiveBuildValue(EffectiveValueKind.ProjectAsset,
                    assets[binding.ProjectAssetId!], null, chain.ToArray()), null),
                BindingKind.InheritedLiveCarrier => (Value(EffectiveValueKind.InheritedLiveCarrier), null),
                BindingKind.Neutral => (Value(EffectiveValueKind.Neutral), null),
                BindingKind.Hidden => (Value(EffectiveValueKind.Hidden), null),
                BindingKind.SourceSlot => ResolveSource(binding.SourceSlot!, backend, assets, slots, edits,
                    visiting, chain),
                _ => (null, DecisionGuard("binding kind was not validated")),
            };
        }
        finally
        {
            chain.RemoveAt(chain.Count - 1);
            visiting.Remove(node);
        }

        EffectiveBuildValue Value(EffectiveValueKind kind) => new(kind, null, null, chain.ToArray());
    }

    private static (EffectiveBuildValue? Value, BuildPlanDecision? Decision) ResolveSource(
        BindingSourceSlot source, IAuthoredBuildBackend backend,
        IReadOnlyDictionary<string, ProjectAsset> assets, IReadOnlyDictionary<string, TargetSlot> slots,
        IReadOnlyDictionary<string, EditDefinition> edits, HashSet<string> visiting, List<string> chain)
    {
        var sourceSlot = slots[source.SlotId];
        if (source.EditDefinitionId is null)
        {
            var current = NormalizeSlot(backend.ResolveSlot(sourceSlot), sourceSlot,
                "source slot " + source.SlotId);
            if (current.Verdict != BuildPlanVerdict.Resolved)
                return (null, BuildPlanDecision.Blocked(current.Verdict, current.Reason, current.Detail));
            return (new EffectiveBuildValue(EffectiveValueKind.SourceGameSlot, null,
                current.CurrentSlot, chain.Append("game:" + source.SlotId).ToArray()), null);
        }

        var sourceEdit = edits[source.EditDefinitionId];
        var sourceBinding = sourceEdit.Bindings.Single(b => b.SlotId == source.SlotId);
        return ResolveEffective(sourceBinding, sourceEdit.Id, backend, assets, slots, edits, visiting, chain);
    }

    /// <summary>One internal-consistency verdict: the plain line the page states, and the account of what
    /// actually disagreed, which only the log sees.</summary>
    private static BuildSlotResolution SlotGuard(TargetSlot? current, string detail) =>
        new(BuildPlanVerdict.Conflict, current, InternalGuard, detail);

    /// <inheritdoc cref="SlotGuard"/>
    private static BuildPlanDecision DecisionGuard(string detail) =>
        BuildPlanDecision.Blocked(BuildPlanVerdict.Conflict, InternalGuard, detail);

    /// <inheritdoc cref="SlotGuard"/>
    private static BuildLifecycleResolution LifecycleGuard(BuildLifecyclePlan? plan, string detail) =>
        new(BuildPlanVerdict.Conflict, plan, InternalGuard, detail);

    private static BuildSlotResolution NormalizeSlot(BuildSlotResolution? resolution,
        TargetSlot authoredSlot, string at)
    {
        if (resolution is null)
            return SlotGuard(null, at + " returned no slot-resolution record");
        if (!Enum.IsDefined(resolution.Verdict))
            return SlotGuard(resolution.CurrentSlot, at + " returned an unknown slot-resolution verdict");
        if (string.IsNullOrWhiteSpace(resolution.Reason))
            return SlotGuard(resolution.CurrentSlot, at + " returned no slot-resolution reason");
        if (resolution.Verdict == BuildPlanVerdict.Resolved && resolution.CurrentSlot is null)
            return SlotGuard(null, at + " resolved without a current-install slot");
        if (resolution.Verdict is BuildPlanVerdict.InheritedAsRequested or BuildPlanVerdict.Unsupported)
            return SlotGuard(resolution.CurrentSlot, at + " returned an invalid identity verdict");
        if (resolution.Verdict == BuildPlanVerdict.Resolved
            && !Corresponds(authoredSlot, resolution.CurrentSlot!))
            return SlotGuard(resolution.CurrentSlot, at + " re-anchored to a different structural slot");
        return resolution;
    }

    private static BuildPlanDecision NormalizeDecision(BuildPlanDecision? decision,
        BuildRuntimeAction expectedAction, string at)
    {
        if (decision is null)
            return DecisionGuard(at + " returned no capability decision");
        if (!Enum.IsDefined(decision.Verdict))
            return DecisionGuard(at + " returned an unknown capability verdict");
        if (string.IsNullOrWhiteSpace(decision.Reason))
            return DecisionGuard(at + " returned no capability reason");
        if (decision.Verdict == BuildPlanVerdict.Resolved)
        {
            if (decision.Action != expectedAction)
                return DecisionGuard($"{at} resolved as {decision.Action}, expected {expectedAction}");
            if (decision.TargetingProof is not { } proof
                || string.IsNullOrWhiteSpace(proof.Kind)
                || string.IsNullOrWhiteSpace(proof.Detail))
                return DecisionGuard(at + " resolved without a targeting proof");
            return decision;
        }
        if (decision.Verdict == BuildPlanVerdict.InheritedAsRequested)
            return expectedAction == BuildRuntimeAction.None && decision.Action == BuildRuntimeAction.None
                ? decision
                : DecisionGuard(at + " inherited a value that requires a runtime action");
        if (decision.Action != BuildRuntimeAction.None || decision.TargetingProof is not null)
            return DecisionGuard(at + " attached a runtime action to a blocking verdict");
        return decision;
    }

    private static BuildOperationResolution NormalizeOperation(BuildOperationResolution? resolution,
        BuildRuntimeAction expectedAction, TargetInputKind input, BuildEmissionGate gate, string at)
    {
        if (resolution is null)
            return new BuildOperationResolution(
                DecisionGuard(at + " returned no operation resolution"), null);
        var decision = NormalizeDecision(resolution.Decision, expectedAction, at);
        var render = resolution.RenderPlan;
        if (decision.Verdict == BuildPlanVerdict.Resolved && render is null)
            return new BuildOperationResolution(DecisionGuard(at + " resolved without a render plan"),
                null, resolution.Emissions, resolution.OutputArtifacts);
        if (render is not null)
        {
            var errors = AuthoredRenderPlanValidator.Errors(render,
                requireComplete: decision.Verdict == BuildPlanVerdict.Resolved);
            if (errors.Count > 0)
                return new BuildOperationResolution(DecisionGuard(
                    at + " returned an invalid render plan: " + string.Join("; ", errors)), render,
                    resolution.Emissions, resolution.OutputArtifacts);
        }
        var operationErrors = AuthoredRenderPlanValidator.OperationErrors(resolution,
            ExpectedEmission(expectedAction, input), gate,
            requireComplete: decision.Verdict == BuildPlanVerdict.Resolved);
        if (operationErrors.Count > 0)
            return new BuildOperationResolution(DecisionGuard(
                at + " returned an invalid emission plan: " + string.Join("; ", operationErrors)), render,
                resolution.Emissions, resolution.OutputArtifacts);
        if (decision.Verdict == BuildPlanVerdict.Resolved)
        {
            var completeRender = render!;
            if (completeRender.BlocksBuild)
                return new BuildOperationResolution(
                    DecisionGuard(at + " resolved with blocking render coverage"), completeRender,
                    resolution.Emissions, resolution.OutputArtifacts);
            var expectedRole = expectedAction == BuildRuntimeAction.Hide
                ? BuildRenderRoleKind.SuppressionTarget : BuildRenderRoleKind.RenderCarrier;
            var role = completeRender.Roles.FirstOrDefault(r => r.Kind == expectedRole
                && r.State == BuildCoverageState.Covered
                && SameProof(r.TargetingProof, decision.TargetingProof));
            if (role is null)
                return new BuildOperationResolution(DecisionGuard(
                    $"{at} targeting proof is not carried by its {expectedRole}"), completeRender,
                    resolution.Emissions, resolution.OutputArtifacts);
            if (expectedRole == BuildRenderRoleKind.RenderCarrier
                && !completeRender.Contracts.Any(c => role.CurrentSlot is not null
                    && string.Equals(c.CarrierSlot.Id, role.CurrentSlot.Id, StringComparison.Ordinal)
                    && SameProof(c.TargetingProof, decision.TargetingProof)))
                return new BuildOperationResolution(DecisionGuard(
                    at + " targeting proof is not carried by its draw contract"), completeRender,
                    resolution.Emissions, resolution.OutputArtifacts);
        }
        return new BuildOperationResolution(decision, render, resolution.Emissions,
            resolution.OutputArtifacts);
    }

    private static BuildLifecycleResolution NormalizeLifecycle(BuildLifecycleResolution? resolution,
        TargetPart target, PlanCondition launch, IReadOnlyList<PlanCondition> acting)
    {
        string at = $"lifecycle for {target.Subject} / {target.Outfit} / {target.RendererSlot}";
        if (resolution is null)
            return LifecycleGuard(null, at + " returned no resolution");
        if (!Enum.IsDefined(resolution.Verdict))
            return LifecycleGuard(resolution.Plan, at + " returned an unknown verdict");
        if (string.IsNullOrWhiteSpace(resolution.Reason))
            return LifecycleGuard(resolution.Plan, at + " returned no reason");
        if (resolution.Verdict == BuildPlanVerdict.InheritedAsRequested)
            return LifecycleGuard(resolution.Plan, at + " returned an invalid inherited verdict");
        if (resolution.Plan is { } plan)
        {
            var errors = AuthoredRenderPlanValidator.LifecycleErrors(plan, launch, acting,
                requireComplete: resolution.Verdict == BuildPlanVerdict.Resolved);
            if (errors.Count > 0)
                return LifecycleGuard(plan,
                    at + " returned an invalid plan: " + string.Join("; ", errors));
        }
        if (resolution.Verdict == BuildPlanVerdict.Resolved)
        {
            if (resolution.Plan is null)
                return LifecycleGuard(null, at + " resolved without a lifecycle plan");
            if (resolution.Plan.BlocksBuild)
                return LifecycleGuard(resolution.Plan, at + " resolved with blocking lifecycle coverage");
        }
        return resolution;
    }

    private static bool HasRuntimeAction(BuildPlanDecision? decision) =>
        decision is { Verdict: BuildPlanVerdict.Resolved, Action: not BuildRuntimeAction.None };

    private static bool SameProof(BuildTargetingProof? left, BuildTargetingProof? right) =>
        left is not null && right is not null
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Detail, right.Detail, StringComparison.Ordinal);

    private static bool Corresponds(TargetSlot authored, TargetSlot current) =>
        authored.Part.SameAs(current.Part)
        && authored.Input == current.Input
        && authored.Domain == current.Domain
        && Same(authored.Tier, current.Tier)
        && authored.SubmeshIndex == current.SubmeshIndex
        && authored.MaterialSlotIndex == current.MaterialSlotIndex
        && Same(authored.Semantic, current.Semantic);

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <param name="diagnostics">Takes the account of every conflict whose own words name emitted ids
    /// rather than anything the modder made.</param>
    private static IReadOnlyList<string> PlanConflicts(IReadOnlyList<PlannedPart> parts,
        IReadOnlyList<PlannedRuntimeEmission> emissions,
        IReadOnlyList<PlannedOutputArtifact> outputs, IssueOwners owners, List<string> diagnostics)
    {
        var conflicts = new List<string>();
        // Which edits each consumer of an emission, an output or a render contract belongs to. A conflict
        // between two consumers is a conflict between the edits behind them, and that is what marks the
        // rows and fills the jump chips — a blocked build with nothing marked leaves the modder hunting.
        var consumerEdits = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            consumerEdits[part.Target.Key + ":hide"] = part.Operations
                .Where(operation => operation.Disposition == PlannedPartDisposition.Hidden
                    && operation.EditDefinitionId is not null)
                .Select(operation => operation.EditDefinitionId!)
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var binding in part.Bindings)
                consumerEdits[binding.RowId] = new[] { binding.EditDefinitionId };
        }
        string[] EditsOf(params string[] consumers) => consumers
            .SelectMany(consumer => consumerEdits.GetValueOrDefault(consumer, Array.Empty<string>()))
            .Distinct(StringComparer.Ordinal).ToArray();
        void Conflict(string line, params string[] editIds)
        {
            conflicts.Add(line);
            if (editIds.Length > 0) owners.Own(line, editIds);
        }
        // Two emitted things claiming one identity. Nothing in it is the modder's — the ids belong to the
        // compiler — so every one of these states the plain line and files its own account for the log.
        // Several such collisions collapse onto ONE row, which then owns all their edits: the row says the
        // same thing however many ways the plan reached it, and the rows it marks are still all of them.
        void GuardConflict(string detail, params string[] editIds)
        {
            Conflict(InternalGuard, editIds);
            diagnostics.Add(detail);
        }

        var geometryClaims = parts.SelectMany(part => part.Bindings.Where(binding =>
                    binding.AuthoredSlot.Input == TargetInputKind.Geometry
                    && binding.Decision is { Verdict: BuildPlanVerdict.Resolved,
                        Action: BuildRuntimeAction.BindProjectAsset }
                    && binding.CurrentSlot?.Mesh is not null)
                .Select(binding => new { Part = part, Binding = binding,
                    Mesh = binding.CurrentSlot!.Mesh! }))
            .GroupBy(claim => $"{claim.Mesh.GameBuild}\u001f{claim.Mesh.LogicalBundle}\u001f{claim.Mesh.PathId}",
                StringComparer.Ordinal);
        // Two replacements on one current mesh conflict unless one key proves they never draw together.
        // States of a single group are that proof; two groups, or a group and an always-on part, are not.
        foreach (var claim in geometryClaims.Where(group => group.Count() > 1))
        {
            var rows = claim.ToList();
            if (rows.All(left => rows.All(right => ReferenceEquals(left, right)
                    || left.Binding.Gate.ProvablyExclusiveOf(right.Binding.Gate))))
                continue;
            string claimed = string.Join(", ", rows.Select(row => PartName(row.Part.Target))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            Conflict($"{rows.Count} mesh replacements are active at once on {claimed}. Use a key to "
                + "switch between them, or remove all but one.",
                rows.Select(row => row.Binding.EditDefinitionId).Distinct(StringComparer.Ordinal).ToArray());
        }
        foreach (var duplicate in emissions.GroupBy(e => e.Emission.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
            GuardConflict($"Runtime emission id '{duplicate.Key}' is claimed {duplicate.Count()} times.",
                EditsOf(duplicate.Select(row => row.Consumer).ToArray()));
        foreach (var duplicate in outputs.GroupBy(o => o.Artifact.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
            GuardConflict($"Output artifact id '{duplicate.Key}' is claimed {duplicate.Count()} times.",
                EditsOf(duplicate.Select(row => row.Consumer).ToArray()));
        foreach (var duplicate in outputs.Where(o => o.Artifact.Included && o.Artifact.File is not null)
            .GroupBy(o => NormalizeFile(o.Artifact.File!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
            GuardConflict($"Included output file '{duplicate.First().Artifact.File}' is claimed "
                + $"{duplicate.Count()} times.", EditsOf(duplicate.Select(row => row.Consumer).ToArray()));
        foreach (var blocked in outputs.Where(o => o.Verdict != BuildPlanVerdict.Resolved
            && o.Artifact.Included))
            GuardConflict($"Blocked consumer '{blocked.Consumer}' claims included output "
                + $"'{blocked.Artifact.Id}'.", EditsOf(blocked.Consumer));

        var contracts = parts.SelectMany(part => Operations(part)
            .Where(row => row.Operation?.RenderPlan is not null)
            .SelectMany(row => row.Operation!.RenderPlan!.Contracts
                .Select(contract => (Contract: contract, row.EditIds))))
            .ToList();
        var contractScopes = new Dictionary<string, IReadOnlyDictionary<string, RenderContract>>(
            StringComparer.Ordinal);
        foreach (var part in parts)
        {
            string partConsumer = part.Target.Key;
            RegisterContracts(part.Suppression, partConsumer + ":hide");
            foreach (var binding in part.Bindings)
                RegisterContracts(binding.Operation, binding.RowId);
        }

        var patchClaims = emissions.Where(emission => emission.Emission.MaterialPatch is not null)
            .SelectMany(emission => ContractsFor(emission).SelectMany(contract =>
                (emission.Emission.MaterialPatch!.Writes ?? Array.Empty<MaterialPatchWrite>())
                .Select(write => new
                {
                    Draw = DrawKey(contract),
                    write.Semantic,
                    write.ByteOffset,
                    emission.Consumer,
                    emission.Emission.Gate,
                    Value = string.Join("|", emission.Emission.MaterialPatch.Layout,
                        emission.Emission.MaterialPatch.ConstantBufferSlot,
                        emission.Emission.MaterialPatch.ByteWidth, write.ByteOffset,
                        BitConverter.SingleToInt32Bits(write.Value)),
                }))).ToList();
        // Keyed by byte offset too: a multi-component field writes one row per component, so
        // grouping on semantic alone compares a colour's own channels against each other.
        foreach (var claim in patchClaims.GroupBy(row => row.Draw + "\u001f" + row.Semantic
            + "\u001f" + row.ByteOffset.ToString(CultureInfo.InvariantCulture),
            StringComparer.Ordinal))
        {
            var rows = claim.OrderBy(row => row.Gate.ToString(), StringComparer.Ordinal)
                .ThenBy(row => row.Value, StringComparer.Ordinal).ToList();
            bool incompatible = false;
            for (int i = 0; i < rows.Count && !incompatible; i++)
                for (int j = i + 1; j < rows.Count; j++)
                    if (!string.Equals(rows[i].Value, rows[j].Value, StringComparison.Ordinal)
                        && !rows[i].Gate.ProvablyExclusiveOf(rows[j].Gate))
                    {
                        incompatible = true;
                        break;
                    }
            if (incompatible)
            {
                var first = rows[0];
                string field = MaterialValueCatalog.Field(first.Semantic)?.Label ?? first.Semantic;
                Conflict($"'{field}' has conflicting values on the same material. Put the edits in "
                    + "different key states, or make the values match.",
                    EditsOf(rows.Select(row => row.Consumer).ToArray()));
            }
        }

        foreach (var draw in contracts.GroupBy(row => DrawKey(row.Contract), StringComparer.Ordinal))
        {
            var variants = draw.Select(row => ContractFingerprint(row.Contract))
                .Distinct(StringComparer.Ordinal).ToList();
            if (variants.Count > 1)
                GuardConflict($"Draw '{draw.Key}' has {variants.Count} incompatible render contracts.",
                    draw.SelectMany(row => row.EditIds).Distinct(StringComparer.Ordinal).ToArray());
        }
        return conflicts.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        static IEnumerable<(BuildOperationResolution? Operation, string[] EditIds)> Operations(
            PlannedPart part) =>
            new[]
            {
                (part.Suppression, part.Operations
                    .Where(operation => operation.Disposition == PlannedPartDisposition.Hidden
                        && operation.EditDefinitionId is not null)
                    .Select(operation => operation.EditDefinitionId!)
                    .Distinct(StringComparer.Ordinal).ToArray()),
            }
                .Concat(part.Operations.Select(operation => (operation.Operation,
                    operation.EditDefinitionId is null ? Array.Empty<string>()
                        : new[] { operation.EditDefinitionId })))
                .Concat(part.Bindings.Select(binding => ((BuildOperationResolution?)binding.Operation,
                    new[] { binding.EditDefinitionId })));

        void RegisterContracts(BuildOperationResolution? operation, string consumer)
        {
            if (operation?.RenderPlan?.Contracts is not { } scoped) return;
            contractScopes[consumer] = scoped
                .GroupBy(contract => contract.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        IEnumerable<RenderContract> ContractsFor(PlannedRuntimeEmission emission)
        {
            if (!contractScopes.TryGetValue(emission.Consumer, out var scoped)) yield break;
            foreach (string id in emission.Emission.RenderContractIds ?? Array.Empty<string>())
                if (scoped.TryGetValue(id, out var contract)) yield return contract;
        }

        static string DrawKey(RenderContract contract) =>
            contract.CarrierSlot.Id + "|" + contract.MaterialCarrierSlot.Id;

        static string ContractFingerprint(RenderContract contract) => string.Join("|",
            Proof(contract.TargetingProof), contract.InputLayout, contract.DrawSpace,
            contract.ShaderFamily, contract.MaterialLayout,
            contract.RenderQueue.ToString(CultureInfo.InvariantCulture), contract.Transparency,
            contract.Stencil, contract.Cull,
            string.Join(",", contract.Passes.OrderBy(p => p.Pass).Select(p => p.Pass + ":" + p.State)),
            string.Join(",", contract.Visibility.Scenes.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(",", contract.Visibility.OutfitStates.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(",", contract.Visibility.Tiers.OrderBy(x => x, StringComparer.Ordinal)),
            contract.Visibility.InstanceScope, contract.Bounds.Basis,
            Values(contract.Bounds.Min), Values(contract.Bounds.Max),
            string.Join(",", (contract.MaterialValueFields ?? Array.Empty<BuildMaterialValueField>())
                .OrderBy(field => field.Semantic, StringComparer.Ordinal)
                .Select(field => string.Join(":", field.Semantic, field.ConstantBufferSlot,
                    field.ByteOffset))),
            string.Join(",", (contract.PixelShaderHashes ?? Array.Empty<string>())
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            contract.PixelShaderFilterIndex,
            contract.RenderStateOwnership, contract.RenderStateReason);

        static string Proof(BuildTargetingProof proof) => proof.Kind + ":" + proof.Detail;
        static string Values(IReadOnlyList<float>? values) => values is null ? "-"
            : string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        static string NormalizeFile(string file) => file.Replace('\\', '/').TrimStart('/');
    }

    private static BuildRuntimeAction ExpectedAction(EffectiveValueKind kind) => kind switch
    {
        EffectiveValueKind.ProjectAsset => BuildRuntimeAction.BindProjectAsset,
        EffectiveValueKind.SourceGameSlot => BuildRuntimeAction.BindGameSource,
        EffectiveValueKind.Neutral => BuildRuntimeAction.GenerateNeutral,
        EffectiveValueKind.Hidden => BuildRuntimeAction.Hide,
        _ => BuildRuntimeAction.None,
    };

    private static BuildEmissionKind ExpectedEmission(BuildRuntimeAction action, TargetInputKind input) =>
        action switch
        {
            BuildRuntimeAction.Hide => BuildEmissionKind.Suppression,
            BuildRuntimeAction.GenerateNeutral => BuildEmissionKind.NeutralBinding,
            BuildRuntimeAction.BindProjectAsset or BuildRuntimeAction.BindGameSource
                when input == TargetInputKind.Geometry => BuildEmissionKind.GeometryReplacement,
            BuildRuntimeAction.BindProjectAsset or BuildRuntimeAction.BindGameSource
                when input == TargetInputKind.MaterialValue => BuildEmissionKind.MaterialValuePatch,
            _ => BuildEmissionKind.ResourceBinding,
        };
}
