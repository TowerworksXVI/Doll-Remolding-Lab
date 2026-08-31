using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>What one compiled work item draws under, in the runtime's own vocabulary.
///
/// <para><see cref="Content"/> is the key position this item's content answers — null for a part no key
/// switches. <see cref="HiddenWhen"/> is the OR-list of positions demanding the part's vanilla draw
/// suppressed on top of that, and <see cref="HiddenBy"/> the hider flag another group raises over it.
/// <see cref="Gate"/> is the plan's own statement of the same thing, carried so the build's hash guards can
/// read exclusivity off the planner rather than deriving a second answer nobody compares against.</para></summary>
public sealed record EditGate(
    KeyRef? Content,
    IReadOnlyList<KeyRef> HiddenWhen,
    string? HiddenBy,
    BuildEmissionGate Gate)
{
    /// <summary>Whether every state of the item's own group suppresses the vanilla draw, so that group's
    /// term drops out of the skip gate entirely. That is exactly what the released two-state
    /// hide-while-off shape says, and saying it this way keeps that shape's emission unchanged.</summary>
    public bool SuppressesInEveryState { get; init; }

    /// <summary>The content flag this item's gate reads instead of a single position term, when its change
    /// answers MORE than one position of its own group (<see cref="ShownFlag.Name"/>). Null where the
    /// change answers exactly one, which its own position states directly.</summary>
    public string? ShownBy { get; init; }
}

/// <summary>One part's key answer as the RELEASED two-state surface spells it, carried for the repair
/// record's compatibility field alone. A group of any other shape has no such projection and states
/// nothing here; the record's own key-group rows say the whole truth beside it.</summary>
internal sealed record ReleasedToggle(string Key, bool HideWhenOff, bool StartsOff);

/// <summary>One compiled work item: a part, the disposition its state answers with, and everything the
/// runtime compiler binds from. Identity is the OBJECT — a part answered the same way in two states is
/// still two pipelines under two gates, and value equality would collapse them into one — so every
/// per-item record the compiler files is keyed on the instance.</summary>
internal sealed class BuildWorkItem
{
    /// <summary>The subject this part belongs to, as the roster resolve spells it.</summary>
    public required string Character { get; init; }

    /// <summary>The outfit stem — matches <see cref="SelectionEntry.Outfit"/>.</summary>
    public required string Outfit { get; init; }

    /// <summary>The roster part's renderer slot name (its representative <c>_lod0</c> slot). LOD-tier
    /// fan-out is NOT stored here — the build enumerates tiers from the live recipe.</summary>
    public required string Mesh { get; init; }

    /// <summary>One of <see cref="EditVerbs"/>: what this state does to the part.</summary>
    public required string Verb { get; init; }

    /// <summary>When this item draws, and what takes it off screen.</summary>
    public required EditGate Gate { get; init; }

    /// <summary>Replace only — the authored donor glb, weighted to the outfit's reference armature.</summary>
    public string? DonorFile { get; init; }

    /// <summary>Replace: per-donor-submesh texture set. Retexture: per-VANILLA-submesh. Read off THIS
    /// state's own bindings, so a part replaced differently in two states never ships one state's pictures
    /// twice.</summary>
    public IReadOnlyList<SubmeshTextures>? Textures { get; init; }

    /// <summary>Replace only — the submesh layout of this state's own replacement
    /// (<see cref="AuthoredDonorRows.MaterialNames"/>), for the repair record.</summary>
    public IReadOnlyList<string>? DonorMaterials { get; init; }

    /// <summary>Replace only — the TARGET mesh's recorded scene-rest uprighting. A fact of the target, not
    /// of what a state answers, so every state of one part carries the same one.</summary>
    public IReadOnlyList<float>? BakedRest { get; init; }

    /// <summary>The target mesh's recorded vertex count, for the repair record.</summary>
    public int? OriginalVerts { get; init; }

    /// <summary>The planned operation this item came from — what a downstream lookup asks when it needs the
    /// bindings of the state this item answers rather than the part's state-0 answer. Null on a suppression
    /// no content edit carries.</summary>
    public PlannedPartOperation? Operation { get; init; }

    /// <inheritdoc cref="ReleasedToggle"/>
    public ReleasedToggle? Toggle { get; init; }

    /// <summary>Every workspace file this item references — what the build existence-checks before
    /// emitting.</summary>
    public IEnumerable<string> ReferencedFiles()
    {
        if (DonorFile is not null) yield return DonorFile;
        if (Textures is not null)
            foreach (var t in Textures)
            {
                if (t.Albedo is not null) yield return t.Albedo;
                if (t.Normal is not null) yield return t.Normal;
                if (t.Rmo is not null) yield return t.Rmo;
                if (t.Ramp is not null) yield return t.Ramp;
                if (t.Blend is not null) yield return t.Blend;
                foreach (var texture in t.Textures ?? new List<PropertyTextureBinding>())
                    if (texture.File is not null) yield return texture.File;
            }
    }
}

/// <summary>The complete input to the production runtime compiler. Change selection, effective sources,
/// capability and file inclusion have already been settled by <see cref="AuthoredBuildPlan"/>.</summary>
public sealed class AuthoredBuildExecution
{
    private AuthoredBuildExecution(AuthoredProject project, AuthoredBuildPlan plan,
        IReadOnlyList<BuildWorkItem> work, IReadOnlyList<StockRampPick> ramps,
        IReadOnlyList<KeyCycle> cycles,
        IReadOnlyList<HiddenFlag> hiddenFlags, IReadOnlyList<ShownFlag> shownFlags,
        IReadOnlyDictionary<StockRampPick, KeyRef?> rampGates,
        IReadOnlyDictionary<StockRampPick, string?> rampShown)
    {
        RampGates = rampGates;
        RampShownFlags = rampShown;
        ShownFlags = shownFlags;
        Project = project;
        Plan = plan;
        Work = work;
        StockRamps = ramps;
        KeyCycles = cycles;
        HiddenFlags = hiddenFlags;
    }

    public AuthoredProject Project { get; }
    public AuthoredBuildPlan Plan { get; }

    /// <summary>One entry per planned operation that emits content, plus one per part some state hides
    /// without owning content of its own. A part answered differently in three states contributes three,
    /// each carrying its own state in its own <see cref="BuildWorkItem.Gate"/> — the compiler builds one
    /// pipeline per entry and gates it ordinally.</summary>
    internal IReadOnlyList<BuildWorkItem> Work { get; }
    internal IReadOnlyList<StockRampPick> StockRamps { get; }

    /// <summary>Every key this build declares, with the cycle its group gives it.</summary>
    internal IReadOnlyList<KeyCycle> KeyCycles { get; }

    /// <summary>The hider flags the emitted content gates read, one per part another group takes off
    /// screen.</summary>
    internal IReadOnlyList<HiddenFlag> HiddenFlags { get; }

    /// <summary>The content flags the emitted content gates read, one per change answering more than one
    /// position of its own key group.</summary>
    internal IReadOnlyList<ShownFlag> ShownFlags { get; }

    /// <summary>The position each entry in <see cref="StockRamps"/> is picked in, by the pick itself.
    /// Null for a pick no key switches.</summary>
    internal IReadOnlyDictionary<StockRampPick, KeyRef?> RampGates { get; }

    /// <summary>The content flag each entry in <see cref="StockRamps"/> binds under, where the change that
    /// picked it answers more than one position. Null for a pick one position answers.</summary>
    internal IReadOnlyDictionary<StockRampPick, string?> RampShownFlags { get; }

    public static AuthoredBuildExecution Create(AuthoredProject project, AuthoredBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        // The page never reaches Build with a blocked plan — ③ lists every blocking line and disables the
        // button — so this is the backstop, and its per-row account names slots and rows. The sentence says
        // what happened; the account rides the exception into the build log.
        if (!plan.CanBuild)
            throw BuildLogDiagnostics.Attach(
                new AuthoredRefusalException("this mod isn't ready to build yet"), BlockedReasons(plan));
        if (project.RootDir is null)
            throw new AuthoredRefusalException("this mod hasn't been saved yet. Save it, then build");

        var work = new List<BuildWorkItem>();
        var ramps = new List<StockRampPick>();
        var flags = new List<HiddenFlag>();
        var shownFlags = new List<ShownFlag>();
        var rampGates = new Dictionary<StockRampPick, KeyRef?>(RampComparer.Instance);
        var rampShown = new Dictionary<StockRampPick, string?>(RampComparer.Instance);
        var cycles = Cycles(project);
        var workspace = new AuthoredWorkspaceFacts(project);
        var released = ReleasedToggles(project);

        foreach (var part in plan.Parts)
        {
            var hideConditions = part.HideConditions;
            bool hiddenAlways = hideConditions.Any(condition => condition.IsAlways);
            var hiddenPositions = hideConditions.Where(condition => !condition.IsAlways)
                .Select(hide => new KeyRef(hide.Key!, hide.StateIndex)).ToList();
            var content = part.Operations
                .Where(operation => operation.Disposition == PlannedPartDisposition.Edit).ToList();
            var liveContent = content.Where(operation => !FullyHidden(operation, hideConditions)).ToList();
            bool singleGroup = part.GroupTouches is { Count: 1 }
                && part.Operations.SelectMany(operation => operation.ActiveWhen)
                    .All(condition => !condition.IsAlways
                        && string.Equals(condition.GroupId, part.GroupTouches[0].GroupId,
                            StringComparison.Ordinal));
            string? flagName = null;
            bool crossGroupHide = hiddenPositions.Count > 0 && liveContent.Any(operation =>
            {
                var groups = operation.ActiveWhen.Where(condition => !condition.IsAlways)
                    .Select(condition => condition.GroupId).Distinct(StringComparer.Ordinal).ToList();
                return operation.ActiveWhen.Any(condition => condition.IsAlways) || groups.Count != 1
                    || hideConditions.Any(hide => !hide.IsAlways
                        && !string.Equals(hide.GroupId, groups[0], StringComparison.Ordinal));
            });
            if (crossGroupHide)
            {
                flagName = FlagName(part.Target, flags.Select(flag => flag.Name));
                flags.Add(new HiddenFlag(flagName, hiddenPositions));
            }

            int emittedContent = 0;
            foreach (var operation in liveContent)
            {
                var positions = operation.ActiveWhen.Any(condition => condition.IsAlways)
                    ? new List<PlanCondition> { PlanCondition.Always }
                    : operation.ActiveWhen.Distinct().ToList();
                string? shownName = null;
                if (positions.Count > 1)
                {
                    shownName = FlagName(part.Target, shownFlags.Select(flag => flag.Name));
                    shownFlags.Add(new ShownFlag(shownName, positions
                        .Select(condition => new KeyRef(condition.Key!, condition.StateIndex)).ToArray()));
                }
                var bindings = operation.Bindings.Where(binding => !binding.Decision.BlocksBuild).ToList();
                foreach (var binding in bindings.Where(binding =>
                             binding.AuthoredSlot.Domain == TargetSlotDomain.Game
                             && binding.AuthoredSlot.Input == TargetInputKind.Ramp
                             && binding.EffectiveValue?.ProjectAsset is { Kind: ProjectAssetKind.Ramp }
                             && binding.CurrentSlot?.Material is not null))
                {
                    var pick = new StockRampPick
                    {
                        Character = part.Target.Subject,
                        Outfit = part.Target.Outfit,
                        Mesh = part.Target.RendererSlot,
                        Material = binding.CurrentSlot!.Material!.Name ?? "",
                        Ramp = binding.EffectiveValue!.ProjectAsset!.File,
                    };
                    ramps.Add(pick);
                    // the pick rides what answers it, exactly as that answer's other bindings do — one
                    // position as a term, several as the content flag raised in each of them
                    rampGates[pick] = shownName is null ? Term(operation.Condition) : null;
                    rampShown[pick] = shownName;
                }

                // Every state of this item's own group answers the part hidden or replaced, so a
                // REPLACEMENT's group term drops out of its skip gate entirely — the released
                // hide-while-off shape, said the same way. Only a replacement can say it: a retexture
                // leaves the part's own draw running, so its hiding positions stay guarded skips.
                bool collapsible = singleGroup
                    && hiddenPositions.Count > 0
                    && positions.Concat(hideConditions.Where(condition => !condition.IsAlways))
                        .Where(condition => string.Equals(condition.GroupId,
                            part.GroupTouches[0].GroupId, StringComparison.Ordinal))
                        .Select(condition => condition.StateIndex).Distinct().Count()
                        >= part.GroupTouches[0].StateCount;
                var gate = new EditGate(shownName is null ? Term(positions[0]) : null,
                    hiddenPositions,
                    flagName,
                    new BuildEmissionGate(operation.ActiveWhen.Select(condition => condition.Term).ToArray(),
                        hideConditions.Select(hide => hide.Term).ToArray()))
                { ShownBy = shownName };
                if (ContentItem(workspace, released, part, operation, bindings, gate, collapsible)
                    is not { } made) continue;
                work.Add(made);
                emittedContent++;
            }

            // With no emitted content, the merged hide placements still suppress the game's own draw.
            if (emittedContent == 0 && hideConditions.Count > 0)
            {
                bool always = hiddenAlways || singleGroup
                    && hideConditions.Select(condition => condition.StateIndex).Distinct().Count()
                        >= part.GroupTouches[0].StateCount;
                work.Add(new BuildWorkItem
                {
                    Character = part.Target.Subject,
                    Outfit = part.Target.Outfit,
                    Mesh = part.Target.RendererSlot,
                    Verb = EditVerbs.Hide,
                    Gate = new EditGate(null, always ? Array.Empty<KeyRef>() : hiddenPositions, null,
                        new BuildEmissionGate(
                            always
                                ? new[] { BuildGateTerm.Always }
                                : hideConditions.Select(condition => condition.Term).ToArray(),
                            Array.Empty<BuildGateTerm>())),
                });
            }
        }

        if (work.Count == 0 && ramps.Count == 0
            && plan.RuntimeEmissions.Any(emission => emission.Emission.Kind
                == BuildEmissionKind.MaterialValuePatch))
            throw new AuthoredRefusalException(
                "material values can only be changed on a part this mod also replaces");
        return new AuthoredBuildExecution(project, plan, work, ramps, cycles, flags,
            shownFlags, rampGates, rampShown);
    }

    /// <summary>One condition as a runtime key position, or null for the condition no key decides.</summary>
    private static KeyRef? Term(PlanCondition condition) =>
        // cast rather than a bare null: see the caution on KeyRef
        condition.IsAlways || condition.Key is null ? (KeyRef?)null
            : new KeyRef(condition.Key, condition.StateIndex);

    private static bool FullyHidden(PlannedPartOperation operation,
        IReadOnlyList<PlanCondition> hideConditions)
    {
        if (hideConditions.Any(condition => condition.IsAlways)) return true;
        if (operation.ActiveWhen.Any(condition => condition.IsAlways)) return false;
        var groups = operation.ActiveWhen.Select(condition => condition.GroupId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (groups is not { Length: 1 } || groups[0] is null) return false;
        return hideConditions.Where(condition => string.Equals(condition.GroupId, groups[0],
                    StringComparison.Ordinal))
                .Select(condition => condition.StateIndex).Distinct().Count()
            >= operation.ActiveWhen[0].StateCount;
    }

    /// <summary>Every key group's cycle, so the emitter declares each key at its group's launch position and
    /// steps it through that group's positions. A group with no usable key switches nothing and declares
    /// nothing.
    ///
    /// <para>The launch position is 0 for every group, because the model says a key's launch state BY
    /// ORDERING: a part that ships off has its content in a later state and state 0 holds what it returns
    /// to. That is what the released starts-off flag becomes when it is adapted, so nothing has to carry a
    /// second, contradictable answer.</para></summary>
    private static IReadOnlyList<KeyCycle> Cycles(AuthoredProject project) =>
        (project.KeyGroups ?? new List<KeyGroup>())
            .Where(group => group.Key is not null && ModKeys.Normalize(group.Key) is not null
                && group.States is { Count: > 0 })
            .Select(group => new KeyCycle(ModKeys.Normalize(group.Key)!, group.States.Count, 0))
            .ToArray();

    /// <summary>The released two-state key answer of every change that has one, by the edit definition it
    /// answers with. <see cref="AuthoredComposition.Head"/> is the whole rule and reads authored intent
    /// directly, so nothing here goes through a projected workspace.
    ///
    /// <para>Only a two-position group holding exactly one content edit for its part has such an answer at
    /// all. Every other shape says nothing here — a longer cycle, two contents, a part nothing but a
    /// suppression answers for — and the repair record's key-group rows state its whole truth beside
    /// it.</para></summary>
    private static Dictionary<string, ReleasedToggle> ReleasedToggles(AuthoredProject project)
    {
        var result = new Dictionary<string, ReleasedToggle>(StringComparer.Ordinal);
        foreach (var entry in AuthoredComposition.Head(project))
            if (entry.Toggle is { } toggle && entry.EditDefinitionId is { } editId
                && ModKeys.Normalize(toggle.Key) is { } key)
                result[editId] = new ReleasedToggle(key, toggle.OffState == CompositionState.Hidden,
                    toggle.StartsOff);
        return result;
    }

    /// <summary>An ini-safe flag name for one part, unique among <paramref name="taken"/>. The hider and
    /// content flags live in separate variable namespaces, so each list is uniquified against itself: two
    /// changes of ONE part each answering several positions is what needs the numbering.</summary>
    private static string FlagName(TargetPart target, IEnumerable<string> taken)
    {
        var claimed = new HashSet<string>(taken, StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (char c in $"{target.Subject}_{target.Outfit}_{target.RendererSlot}")
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        string stem = sb.ToString(), pick = stem;
        for (int n = 2; claimed.Contains(pick); n++) pick = $"{stem}_{n}";
        return pick;
    }

    /// <summary>One state's content as a work item: a geometry replacement when the state binds one, else a
    /// retexture of whatever map rows it states. Null when the state's answer needs neither, which is a
    /// binding set the runtime has nothing to emit for.</summary>
    private static BuildWorkItem? ContentItem(AuthoredWorkspaceFacts workspace,
        IReadOnlyDictionary<string, ReleasedToggle> released, PlannedPart part,
        PlannedPartOperation operation, IReadOnlyList<PlannedBinding> bindings, EditGate gate,
        bool collapsible)
    {
        var rows = bindings.Select(binding => new EditOutputRow(binding.RequestedBinding,
            binding.AuthoredSlot, binding.EffectiveValue?.ProjectAsset)).ToList();
        var toggle = operation.EditDefinitionId is { } editId
            ? released.GetValueOrDefault(editId) : null;
        var geometry = bindings.FirstOrDefault(binding =>
            binding.AuthoredSlot.Input == TargetInputKind.Geometry
            && (binding.AuthoredSlot.Tier is null || string.Equals(binding.AuthoredSlot.Tier,
                "lod0", StringComparison.OrdinalIgnoreCase)));
        if (geometry?.EffectiveValue?.ProjectAsset is { } geometryAsset)
            return new BuildWorkItem
            {
                Character = part.Target.Subject,
                Outfit = part.Target.Outfit,
                Mesh = part.Target.RendererSlot,
                Verb = EditVerbs.Replace,
                DonorFile = geometryAsset.File,
                // read off THIS state's own bindings: a part replaced differently in two states would
                // otherwise ship one state's pictures and one state's submesh layout twice
                Textures = AuthoredDonorRows.Rows(rows),
                DonorMaterials = AuthoredDonorRows.MaterialNames(rows),
                // the recorded uprighting and vertex count are facts of the TARGET mesh, not of what a
                // state answers
                BakedRest = workspace.BakedRestOf(part.Target),
                OriginalVerts = workspace.OriginalVerticesOf(part.Target),
                Gate = collapsible
                    ? gate with { HiddenWhen = Array.Empty<KeyRef>(), SuppressesInEveryState = true }
                    : gate,
                Operation = operation,
                Toggle = toggle,
            };

        var maps = MapRows(bindings);
        return maps.Count == 0 ? null : new BuildWorkItem
        {
            Character = part.Target.Subject,
            Outfit = part.Target.Outfit,
            Mesh = part.Target.RendererSlot,
            Verb = EditVerbs.Retexture,
            Textures = maps,
            // a retexture keeps the part's own draw running, so it collapses nothing: the released
            // hide-while-off shape is a REPLACEMENT's answer and no other verb can state it
            Gate = gate,
            Operation = operation,
            Toggle = toggle,
        };
    }

    private static List<SubmeshTextures> MapRows(IReadOnlyList<PlannedBinding> bindings)
    {
        var rows = new Dictionary<int, SubmeshTextures>();
        var fixedKinds = new HashSet<(int Submesh, TargetInputKind Input)>();
        foreach (var binding in bindings.Where(binding =>
                     binding.AuthoredSlot.Domain == TargetSlotDomain.Game
                     && binding.AuthoredSlot.SubmeshIndex is not null
                     && binding.AuthoredSlot.Input is TargetInputKind.BaseColor
                         or TargetInputKind.Normal or TargetInputKind.Rmo or TargetInputKind.Blend
                         or TargetInputKind.Texture))
        {
            int submesh = binding.AuthoredSlot.SubmeshIndex!.Value;
            if (!rows.TryGetValue(submesh, out var row))
                rows[submesh] = row = new SubmeshTextures { Submesh = submesh };
            string? file = binding.EffectiveValue?.ProjectAsset?.File;
            SlotOrigin origin = file is not null ? SlotOrigin.Authored
                : binding.EffectiveValue?.Kind == EffectiveValueKind.Neutral
                    ? SlotOrigin.ExplicitNeutral : SlotOrigin.VanillaOwn;
            bool additionalExactFixed = binding.AuthoredSlot.Input is TargetInputKind.BaseColor
                    or TargetInputKind.Normal or TargetInputKind.Rmo or TargetInputKind.Blend
                && binding.AuthoredSlot.ShaderProperty is { Length: > 0 }
                && !fixedKinds.Add((submesh, binding.AuthoredSlot.Input));
            if (additionalExactFixed)
            {
                row.Textures ??= new List<PropertyTextureBinding>();
                row.Textures.Add(new PropertyTextureBinding
                {
                    ShaderProperty = binding.AuthoredSlot.ShaderProperty!,
                    File = file,
                    Origin = origin,
                });
                continue;
            }
            switch (binding.AuthoredSlot.Input)
            {
                case TargetInputKind.BaseColor: row.Albedo = file; row.AlbedoOrigin = origin; break;
                case TargetInputKind.Normal: row.Normal = file; row.NormalOrigin = origin; break;
                case TargetInputKind.Rmo: row.Rmo = file; row.RmoOrigin = origin; break;
                case TargetInputKind.Blend: row.Blend = file; row.BlendOrigin = origin; break;
                case TargetInputKind.Texture when binding.AuthoredSlot.ShaderProperty is { Length: > 0 } property:
                    row.Textures ??= new List<PropertyTextureBinding>();
                    row.Textures.Add(new PropertyTextureBinding
                    {
                        ShaderProperty = property,
                        File = file,
                        Origin = origin,
                    });
                    break;
            }
        }
        return rows.Values.OrderBy(row => row.Submesh).ToList();
    }

    /// <summary>Two picks are the same only when they ARE the same object: one material picked in two
    /// states is two binds under two gates, and value equality would collapse them into one.</summary>
    private sealed class RampComparer : IEqualityComparer<StockRampPick>
    {
        public static RampComparer Instance { get; } = new();
        public bool Equals(StockRampPick? x, StockRampPick? y) => ReferenceEquals(x, y);
        public int GetHashCode(StockRampPick obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static string BlockedReasons(AuthoredBuildPlan plan)
    {
        var reasons = plan.Bindings.Where(binding => binding.Decision.BlocksBuild)
            .Select(binding => $"{binding.RowId}: {binding.Decision.Reason}")
            .Concat(plan.Parts.SelectMany(part => part.Operations
                .Select(operation => operation.Operation?.Decision)
                .Append(part.Suppression?.Decision)
                .Where(decision => decision?.BlocksBuild == true)
                .Select(decision => part.Target.Key + ": " + decision!.Reason)
                .Concat(part.Lifecycle?.BlocksBuild == true
                    ? new[] { part.Target.Key + ": " + part.Lifecycle.Reason }
                    : Array.Empty<string>())))
            .Concat(plan.Conflicts).Distinct(StringComparer.Ordinal).ToList();
        return reasons.Count == 0 ? "something in it can't be built yet" : string.Join("; ", reasons);
    }
}
