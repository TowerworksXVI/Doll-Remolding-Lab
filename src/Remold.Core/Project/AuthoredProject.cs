using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remold.Core.Project;

/// <summary>The authored project shape consumed by resolution. It contains intent only: game-install
/// hashes, shader layouts, carriers and emitted files belong to later derived records.</summary>
public sealed class AuthoredProject
{
    public const int CurrentSchema = 2;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    [JsonPropertyName("info")] public ProjectInfo Info { get; set; } = new();
    [JsonPropertyName("authored_against")] public AuthoredAgainst? AuthoredAgainst { get; set; }
    [JsonPropertyName("project_assets")] public List<ProjectAsset> ProjectAssets { get; set; } = new();
    [JsonPropertyName("target_slots")] public List<TargetSlot> TargetSlots { get; set; } = new();
    [JsonPropertyName("edit_definitions")] public List<EditDefinition> EditDefinitions { get; set; } = new();
    [JsonPropertyName("always")] public List<string> Always { get; set; } = new();
    [JsonPropertyName("key_groups")] public List<KeyGroup> KeyGroups { get; set; } = new();
    /// <summary>Materialized workspace inventory used to project the existing one-edit-per-part controls.
    /// It carries no edit, visibility, build-inclusion, key or ramp decisions; those are reconstructed
    /// exclusively from <see cref="EditDefinitions"/>, <see cref="Always"/> and
    /// <see cref="KeyGroups"/>.</summary>
    [JsonPropertyName("workspace_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuthoredWorkspaceIndex? WorkspaceIndex { get; set; }

    [JsonIgnore] public string? RootDir { get; set; }

    /// <summary>Resolve a workspace-relative path against <see cref="RootDir"/>.</summary>
    public string Resolve(string relative)
    {
        if (RootDir is null)
            throw new InvalidOperationException("project has no RootDir (load or save it first)");
        return Path.GetFullPath(Path.Combine(RootDir, relative));
    }
}

/// <summary>One immutable object in one game build. The name is display metadata; the build, logical
/// bundle and path id are its identity.</summary>
public sealed class GameAssetRef
{
    [JsonPropertyName("game_build")] public string GameBuild { get; set; } = "";
    [JsonPropertyName("logical_bundle")] public string LogicalBundle { get; set; } = "";
    [JsonPropertyName("path_id")] public long PathId { get; set; }
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

public enum ProjectAssetKind
{
    Unknown,
    Geometry,
    Picture,
    Ramp,
    StructuredValue,
}

/// <summary>An authored file or structured value owned by the project. Lineage explains where it came
/// from and never selects a target slot.</summary>
public sealed class ProjectAsset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public ProjectAssetKind Kind { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectAssetSource? Source { get; set; }
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectAssetValue? Value { get; set; }
}

/// <summary>A narrow semantic authored value. The semantic is the portable identity; a backend-specific
/// register or offset is never persisted here.</summary>
public sealed class ProjectAssetValue
{
    [JsonPropertyName("semantic")] public string Semantic { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

/// <summary>Optional provenance for a project asset. Exactly one member is populated when present.</summary>
public sealed class ProjectAssetSource
{
    [JsonPropertyName("game_asset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameAssetRef? GameAsset { get; set; }

    [JsonPropertyName("project_asset_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectAssetId { get; set; }
}

/// <summary>The cross-build structural identity of one logical renderer/part.</summary>
public sealed class TargetPart
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    [JsonPropertyName("renderer_slot")] public string RendererSlot { get; set; } = "";

    public bool SameAs(TargetPart? other) => other is not null
        && string.Equals(Subject, other.Subject, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Outfit, other.Outfit, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RendererSlot, other.RendererSlot, StringComparison.OrdinalIgnoreCase);

    internal string Key => $"{Subject}\u001f{Outfit}\u001f{RendererSlot}";
}

public enum TargetInputKind
{
    Unknown,
    Geometry,
    BaseColor,
    Normal,
    Rmo,
    RmoAlpha,
    Ramp,
    Visibility,
    MaterialValue,
    /// <summary>The shader's effect overlay (<c>_BlendTex</c>): the hair specular band, the face
    /// blush/expression tint. An ordinary picture slot, authored and shipped the way base
    /// colour/normal/RMO are.</summary>
    Blend,
    /// <summary>An ordinary texture binding with no known special semantics. The exact shader property is
    /// the slot identity; this coarse value says only that the binding takes an ordinary picture.</summary>
    Texture,
}

/// <summary>Whether a slot belongs to the installed game object or to geometry created by this edit.
/// Replacement submeshes own their material inputs; stock texture and ramp choices address game slots.</summary>
public enum TargetSlotDomain
{
    Game,
    EditOutput,
}

/// <summary>An exact place a value can be bound. Structural fields are the cross-build rejoin route;
/// embedded game references pin the objects selected in the build where that route was resolved.</summary>
public sealed class TargetSlot
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Pre-transpose filing metadata retained for input compatibility only. Domain and the
    /// structural route, never this value, determine what a slot is and whether two slots correspond.</summary>
    [JsonPropertyName("owner_edit_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerEditId { get; set; }
    [JsonPropertyName("part")] public TargetPart Part { get; set; } = new();
    [JsonPropertyName("tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tier { get; set; }
    [JsonPropertyName("submesh_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SubmeshIndex { get; set; }
    [JsonPropertyName("material_slot_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaterialSlotIndex { get; set; }
    [JsonPropertyName("input")] public TargetInputKind Input { get; set; }
    /// <summary>The material's exact shader-property name for a texture binding. Optional only for
    /// compatibility with schema-2 rows written before texture slots carried their property; every newly
    /// resolved texture slot records it. Presentation tables may label this value but never enumerate by
    /// them.</summary>
    [JsonPropertyName("shader_property")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShaderProperty { get; set; }
    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TargetSlotDomain Domain { get; set; }
    [JsonPropertyName("semantic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Semantic { get; set; }
    [JsonPropertyName("renderer")] public GameAssetRef Renderer { get; set; } = new();
    [JsonPropertyName("mesh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameAssetRef? Mesh { get; set; }
    [JsonPropertyName("material")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameAssetRef? Material { get; set; }

    /// <summary>The current install's index count at this material position. Derived during re-anchoring,
    /// never authored or persisted; zero proves that the game submits no carrier geometry here.</summary>
    [JsonIgnore] public int? DrawIndexCount { get; set; }

    /// <summary>The current install's complete material draw shape on the lod0 geometry anchor. Runtime
    /// evidence used to fold replacement submeshes onto installed material positions; never authored or
    /// persisted.</summary>
    [JsonIgnore] public IReadOnlyList<int>? MaterialIndexCounts { get; set; }

    /// <summary>Whether the current install still binds this material property. Null means the part has not
    /// been re-anchored in this process; false preserves a stale authored route for diagnostics/build repair
    /// while keeping it out of the current card inventory. Never persisted.</summary>
    [JsonIgnore] public bool? MaterialBindingPresent { get; set; }

    /// <summary>Whether two slots address the same place: the cross-build structural route, the domain and
    /// the material-value semantic. Transient filing metadata is deliberately not asked. This is the
    /// identity a command asking "does the project already know this slot" wants.</summary>
    public bool SameRoute(TargetSlot? other) => other is not null
        && Part.SameAs(other.Part)
        && string.Equals(Tier, other.Tier, StringComparison.OrdinalIgnoreCase)
        && SubmeshIndex == other.SubmeshIndex
        && MaterialSlotIndex == other.MaterialSlotIndex
        && Input == other.Input
        && Domain == other.Domain
        // A missing property is the legacy spelling of a known input. It is a wildcard only during
        // re-anchoring, where EnsurePartSlots immediately enriches it from the installed binding. A generic
        // Texture can never take this arm because validation requires its property.
        && (string.IsNullOrWhiteSpace(ShaderProperty)
            || string.IsNullOrWhiteSpace(other.ShaderProperty)
            || string.Equals(ShaderProperty, other.ShaderProperty, StringComparison.Ordinal))
        && string.Equals(Semantic, other.Semantic, StringComparison.Ordinal);
}

public enum BindingKind
{
    Unknown,
    TargetGameValue,
    ProjectAsset,
    SourceSlot,
    InheritedLiveCarrier,
    Neutral,
    Hidden,
}

/// <summary>An exact game slot when <see cref="EditDefinitionId"/> is absent, or a slot owned by the
/// named edit definition when it is present.</summary>
public sealed class BindingSourceSlot
{
    [JsonPropertyName("slot_id")] public string SlotId { get; set; } = "";
    [JsonPropertyName("edit_definition_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EditDefinitionId { get; set; }
}

/// <summary>One explicit request for one slot. Unsupported, unresolved and conflict are resolver
/// verdicts and are deliberately not binding kinds.</summary>
public sealed class Binding
{
    [JsonPropertyName("slot_id")] public string SlotId { get; set; } = "";
    [JsonPropertyName("kind")] public BindingKind Kind { get; set; }
    [JsonPropertyName("project_asset_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectAssetId { get; set; }
    [JsonPropertyName("source_slot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindingSourceSlot? SourceSlot { get; set; }
}

public enum EditDefinitionKind
{
    Content,
    Hide,
}

/// <summary>One named, project-owned answer for one logical renderer/part. Hide is a first-class
/// alternative so it can coexist with content edits and later participate in the same option cycle.</summary>
public sealed class EditDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EditDefinitionKind Kind { get; set; }
    [JsonPropertyName("target")] public TargetPart Target { get; set; } = new();
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("return_warning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnWarning { get; set; }
    [JsonPropertyName("bindings")] public List<Binding> Bindings { get; set; } = new();
}

/// <summary>One runtime key and the ordered states it cycles through. A missing key is valid authored intent
/// but cannot build until the card is assigned one.</summary>
public sealed class KeyGroup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }
    [JsonPropertyName("states")] public List<KeyGroupState> States { get; set; } = new();
}

/// <summary>One stable, ordered position of a key group's cycle. Absence means the game's own draw.</summary>
public sealed class KeyGroupState
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }
    [JsonPropertyName("active_edit_ids")] public List<string> ActiveEditIds { get; set; } = new();
}

/// <summary>Non-authoritative materialization metadata needed by the current Edit surface. Authored
/// decisions are deliberately absent, so this index cannot become a second project model.</summary>
public sealed class AuthoredWorkspaceIndex
{
    [JsonPropertyName("selection")] public List<SelectionEntry> Selection { get; set; } = new();
    [JsonPropertyName("records")] public List<AuthoredWorkspaceRecord> Records { get; set; } = new();

    /// <summary>The earlier schema-2 workspace cache. It is accepted only while deserializing an older
    /// manifest, normalized into <see cref="Records"/>, and cleared before validation or serialization.</summary>
    [JsonPropertyName("targets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectTarget>? LegacyTargets { get; set; }

}

/// <summary>One materialized workspace artifact addressed by structural part, exact game object and,
/// where the authored model already has it, exact slot id. It carries cache/baseline facts only; edit
/// selection, bindings, fan-out users and edited state are deliberately absent.</summary>
public sealed class AuthoredWorkspaceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public ProjectAssetKind Kind { get; set; }
    [JsonPropertyName("part")] public TargetPart Part { get; set; } = new();
    [JsonPropertyName("game_asset")] public GameAssetRef GameAsset { get; set; } = new();
    [JsonPropertyName("slot_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SlotId { get; set; }
    [JsonPropertyName("project_file")] public string ProjectFile { get; set; } = "";
    [JsonPropertyName("baseline_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineFile { get; set; }
    [JsonPropertyName("original_vertices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OriginalVertices { get; set; }
    [JsonPropertyName("lod_slots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Export.LodSlot>? LodSlots { get; set; }
    [JsonPropertyName("baked_rest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<float>? BakedRest { get; set; }
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
}

internal static class AuthoredWorkspaceNormalizer
{
    internal static void Normalize(AuthoredProject project)
    {
        var index = project.WorkspaceIndex;
        if (index?.LegacyTargets is not { } legacy) return;

        index.Records ??= new List<AuthoredWorkspaceRecord>();
        int next = 1;
        var taken = index.Records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var target in legacy)
            foreach (var candidate in Candidates(project, target))
            {
                if (index.Records.Any(record => SameRecord(record, candidate))) continue;
                while (!taken.Add(candidate.Id = $"workspace-{next++:D4}")) { }
                index.Records.Add(candidate);
            }
        index.LegacyTargets = null;
    }

    private static IEnumerable<AuthoredWorkspaceRecord> Candidates(AuthoredProject project,
        ProjectTarget target)
    {
        if (!AuthoredProjectValidator.IsProjectRelativeFile(target.ReplaceFile)) yield break;
        bool geometry = string.Equals(target.AssetType, "Mesh", StringComparison.OrdinalIgnoreCase);
        var slots = project.TargetSlots.Where(slot => geometry
                ? slot.Input == TargetInputKind.Geometry && MatchesPart(target, slot.Part)
                : slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
                    or TargetInputKind.Blend or TargetInputKind.Ramp or TargetInputKind.Texture)
            .ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var assets = project.ProjectAssets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var bound = project.EditDefinitions.SelectMany(edit => edit.Bindings)
            .Where(binding => slots.ContainsKey(binding.SlotId) && binding.ProjectAssetId is not null
                && assets.TryGetValue(binding.ProjectAssetId, out var asset)
                && SameFile(asset.File, target.ReplaceFile)
                && asset.Source?.GameAsset is not null)
            .Select(binding => (Slot: slots[binding.SlotId],
                Game: assets[binding.ProjectAssetId!].Source!.GameAsset!))
            .Where(value => geometry || MatchesLegacyTarget(target, value.Game))
            .DistinctBy(value => (value.Slot.Id, GameKey(value.Game))).ToList();

        if (geometry)
        {
            var slot = slots.Values.OrderBy(value => value.Tier is null
                    || string.Equals(value.Tier, "lod0", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault();
            if (slot?.Mesh is not null)
                yield return Record(target, slot.Part, slot.Mesh, slot.Id, ProjectAssetKind.Geometry);
            yield break;
        }

        foreach (var value in bound)
            yield return Record(target, value.Slot.Part, value.Game, value.Slot.Id,
                target.ObjectName.Contains("ramp", StringComparison.OrdinalIgnoreCase)
                    ? ProjectAssetKind.Ramp : ProjectAssetKind.Picture);

        if (bound.Count > 0) yield break;
        if (target.PathId is not { } pathId || pathId == 0 || string.IsNullOrWhiteSpace(target.Bundle))
            yield break;
        string build = project.TargetSlots.Select(slot => slot.Renderer.GameBuild)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? project.AuthoredAgainst?.CatalogVersion ?? "legacy-schema-2";
        IEnumerable<string> users = target.Users is { Count: > 0 }
            ? target.Users : new[] { target.ObjectName };
        foreach (string user in users)
            yield return Record(target, new TargetPart
            {
                Subject = target.SubjectCharacter ?? project.Info.Character ?? "legacy",
                Outfit = target.SubjectOutfit ?? project.Info.Outfit ?? "legacy",
                RendererSlot = user,
            }, new GameAssetRef
            {
                GameBuild = build,
                LogicalBundle = target.Bundle,
                PathId = pathId,
                Name = target.ObjectName,
            }, null, ProjectAssetKind.Picture);
    }

    private static AuthoredWorkspaceRecord Record(ProjectTarget source, TargetPart part,
        GameAssetRef game, string? slotId, ProjectAssetKind kind) => new()
    {
        Kind = kind,
        Part = Clone(part),
        GameAsset = Clone(game),
        SlotId = slotId,
        ProjectFile = NormalizeFile(source.ReplaceFile),
        BaselineFile = AuthoredProjectValidator.IsProjectRelativeFile(source.OriginalFile)
            ? NormalizeFile(source.OriginalFile!) : null,
        OriginalVertices = source.OriginalVerts,
        LodSlots = source.LodSlots?.Select(slot => new Export.LodSlot
        {
            ObjectName = slot.ObjectName, Bundle = slot.Bundle, PathId = slot.PathId,
        }).ToList(),
        BakedRest = source.BakedRest?.ToList(),
        Source = source.Source,
    };

    private static bool MatchesPart(ProjectTarget target, TargetPart part) =>
        (string.IsNullOrWhiteSpace(target.SubjectCharacter)
            || string.Equals(target.SubjectCharacter, part.Subject, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(target.SubjectOutfit)
            || string.Equals(target.SubjectOutfit, part.Outfit, StringComparison.OrdinalIgnoreCase))
        && string.Equals(target.ObjectName, part.RendererSlot, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesLegacyTarget(ProjectTarget target, GameAssetRef game) =>
        (target.PathId is null || target.PathId == game.PathId)
        && (string.IsNullOrWhiteSpace(target.Bundle)
            || string.Equals(target.Bundle, game.LogicalBundle, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(target.ObjectName)
            || string.Equals(target.ObjectName, game.Name, StringComparison.OrdinalIgnoreCase));

    private static bool SameRecord(AuthoredWorkspaceRecord left, AuthoredWorkspaceRecord right) =>
        left.Part.SameAs(right.Part)
        && left.Kind == right.Kind
        && string.Equals(left.SlotId, right.SlotId, StringComparison.Ordinal)
        && string.Equals(GameKey(left.GameAsset), GameKey(right.GameAsset), StringComparison.Ordinal)
        && SameFile(left.ProjectFile, right.ProjectFile);

    private static bool SameFile(string left, string right) => string.Equals(NormalizeFile(left),
        NormalizeFile(right), StringComparison.OrdinalIgnoreCase);
    private static string NormalizeFile(string file) => file.Replace('\\', '/');
    private static string GameKey(GameAssetRef value) =>
        $"{value.GameBuild}\u001f{value.LogicalBundle}\u001f{value.PathId}";
    private static TargetPart Clone(TargetPart source) => new()
    {
        Subject = source.Subject, Outfit = source.Outfit, RendererSlot = source.RendererSlot,
    };
    private static GameAssetRef Clone(GameAssetRef source) => new()
    {
        GameBuild = source.GameBuild, LogicalBundle = source.LogicalBundle,
        PathId = source.PathId, Name = source.Name,
    };
}

/// <summary>Structural validation for schema-2 authored intent. It rejects ambiguous identity and
/// malformed references before resolution can mistake them for a capability result.</summary>
public static class AuthoredProjectValidator
{
    public static IReadOnlyList<string> Errors(AuthoredProject project)
    {
        var errors = new List<string>();
        if (project.Schema != AuthoredProject.CurrentSchema)
            errors.Add($"schema must be {AuthoredProject.CurrentSchema}");
        if (project.Info is null) errors.Add("info is required");
        if (project.Always is null) errors.Add("always list is required");

        var projectAssets = project.ProjectAssets ?? new List<ProjectAsset>();
        var targetSlots = project.TargetSlots ?? new List<TargetSlot>();
        var editDefinitions = project.EditDefinitions ?? new List<EditDefinition>();
        var assets = Unique(project.ProjectAssets, a => a.Id, "project asset", errors);
        var slots = Unique(project.TargetSlots, s => s.Id, "target slot", errors);
        var edits = Unique(project.EditDefinitions, e => e.Id, "edit definition", errors);

        if (project.WorkspaceIndex is { } workspace)
        {
            if (workspace.LegacyTargets is not null)
                errors.Add("workspace index still contains legacy targets");
            var records = Unique(workspace.Records, record => record.Id, "workspace record", errors);
            foreach (var record in (workspace.Records ?? new List<AuthoredWorkspaceRecord>())
                         .Where(record => record is not null))
            {
                string at = $"workspace record '{record.Id}'";
                if (record.Kind is not (ProjectAssetKind.Geometry or ProjectAssetKind.Picture
                    or ProjectAssetKind.Ramp)) errors.Add($"{at} has unsupported kind {record.Kind}");
                ValidatePart(record.Part, at, errors);
                ValidateGameRef(record.GameAsset, $"{at} game asset", errors);
                if (!IsProjectRelativeFile(record.ProjectFile)) errors.Add($"{at} has an invalid project file");
                if (record.BaselineFile is not null && !IsProjectRelativeFile(record.BaselineFile))
                    errors.Add($"{at} has an invalid baseline file");
                if (record.SlotId is { } slotId)
                {
                    if (!slots.TryGetValue(slotId, out var slot))
                        errors.Add($"{at} names missing target slot '{slotId}'");
                    else if (!slot.Part.SameAs(record.Part))
                        errors.Add($"{at} names a slot on another part");
                }
            }
        }

        foreach (var asset in projectAssets.Where(a => a is not null))
        {
            string at = $"project asset '{asset.Id}'";
            if (asset.Kind == ProjectAssetKind.Unknown) errors.Add($"{at} has no kind");
            if (string.IsNullOrWhiteSpace(asset.Label)) errors.Add($"{at} has no label");
            if (!IsProjectRelativeFile(asset.File)) errors.Add($"{at} has an invalid project file");
            if (asset.Kind == ProjectAssetKind.StructuredValue)
            {
                if (asset.Value is null || string.IsNullOrWhiteSpace(asset.Value.Semantic)
                    || string.IsNullOrWhiteSpace(asset.Value.Value))
                    errors.Add($"{at} has no semantic structured value");
            }
            else if (asset.Value is not null)
                errors.Add($"{at} carries a structured value for {asset.Kind}");
            if (asset.Source is not null)
            {
                int sources = (asset.Source.GameAsset is null ? 0 : 1)
                    + (string.IsNullOrWhiteSpace(asset.Source.ProjectAssetId) ? 0 : 1);
                if (sources != 1) errors.Add($"{at} lineage must name exactly one source");
                if (asset.Source.GameAsset is not null)
                    ValidateGameRef(asset.Source.GameAsset, $"{at} source", errors);
                if (asset.Source.ProjectAssetId is { } sourceId && !assets.ContainsKey(sourceId))
                    errors.Add($"{at} names missing source project asset '{sourceId}'");
                if (string.Equals(asset.Id, asset.Source.ProjectAssetId, StringComparison.Ordinal))
                    errors.Add($"{at} cannot be its own source");
            }
        }
        ValidateLineageCycles(projectAssets, assets, errors);

        foreach (var slot in targetSlots.Where(s => s is not null))
        {
            string at = $"target slot '{slot.Id}'";
            ValidatePart(slot.Part, at, errors);
            if (slot.Input == TargetInputKind.Unknown) errors.Add($"{at} has no input kind");
            if (slot.Input == TargetInputKind.Texture && string.IsNullOrWhiteSpace(slot.ShaderProperty))
                errors.Add($"{at} has no shader property");
            if (!Enum.IsDefined(slot.Domain)) errors.Add($"{at} has an unknown domain");
            if (slot.Domain == TargetSlotDomain.EditOutput && slot.Material is not null)
                errors.Add($"{at} edit-output slot carries a game material reference");
            if (slot.SubmeshIndex < 0) errors.Add($"{at} has a negative submesh index");
            if (slot.MaterialSlotIndex < 0) errors.Add($"{at} has a negative material-slot index");
            if (slot.Input == TargetInputKind.MaterialValue && string.IsNullOrWhiteSpace(slot.Semantic))
                errors.Add($"{at} has no material-value semantic");
            if (slot.Input != TargetInputKind.MaterialValue && slot.Semantic is not null)
                errors.Add($"{at} carries a semantic for a non-material input");
            ValidateGameRef(slot.Renderer, $"{at} renderer", errors);
            if (slot.Input == TargetInputKind.Geometry && slot.Mesh is null)
                errors.Add($"{at} has no exact mesh reference");
            if (slot.Mesh is not null) ValidateGameRef(slot.Mesh, $"{at} mesh", errors);
            if (NeedsMaterial(slot.Input) && slot.Domain == TargetSlotDomain.Game && slot.Material is null)
                errors.Add($"{at} has no exact material reference");
            if (slot.Material is not null) ValidateGameRef(slot.Material, $"{at} material", errors);
        }

        // Duplicate structural routes are valid across different edits, but one edit cannot answer the same
        // place twice.
        foreach (var edit in editDefinitions.Where(e => e is not null))
        {
            string at = $"edit definition '{edit.Id}'";
            ValidatePart(edit.Target, at, errors);
            if (string.IsNullOrWhiteSpace(edit.Label)) errors.Add($"{at} has no label");
            var bound = new HashSet<string>(StringComparer.Ordinal);
            var bindings = (edit.Bindings ?? new List<Binding>()).Where(b => b is not null).ToList();
            foreach (var binding in bindings)
            {
                string bat = $"{at} binding '{binding.SlotId}'";
                if (string.IsNullOrWhiteSpace(binding.SlotId))
                {
                    errors.Add($"{at} has a binding with no slot id");
                    continue;
                }
                if (!bound.Add(binding.SlotId)) errors.Add($"{at} binds slot '{binding.SlotId}' more than once");
                if (!slots.TryGetValue(binding.SlotId, out var slot))
                {
                    errors.Add($"{bat} names a missing target slot");
                    continue;
                }
                if (slot.Part is null || !slot.Part.SameAs(edit.Target)) errors.Add($"{bat} targets another part");
                ValidateBinding(binding, slot, assets, slots, edits, bat, errors);
            }

            var boundSlots = bindings.Where(binding => slots.ContainsKey(binding.SlotId))
                .Select(binding => slots[binding.SlotId]).ToList();
            for (int i = 0; i < boundSlots.Count; i++)
                for (int j = i + 1; j < boundSlots.Count; j++)
                    if (boundSlots[i].SameRoute(boundSlots[j]))
                        errors.Add($"{at} binds the same route through slots '{boundSlots[i].Id}' and "
                            + $"'{boundSlots[j].Id}'");

            bool hide = edit.Kind == EditDefinitionKind.Hide;
            if (hide)
            {
                var hidden = bindings.Where(binding => slots.TryGetValue(binding.SlotId, out var slot)
                    && slot.Input == TargetInputKind.Visibility && binding.Kind == BindingKind.Hidden).ToList();
                if (bindings.Count != 1 || hidden.Count != 1)
                    errors.Add($"{at} hide edit must contain exactly one hidden visibility binding");
            }
            // A content edit binds geometry, pictures, ramp and material values, never visibility: whether a
            // part draws at all is a hide's one answer, so a content edit may not reach a visibility slot
            // with any binding, and is not owed one either.
            else if (bindings.Any(binding => slots.TryGetValue(binding.SlotId, out var slot)
                         && slot.Input == TargetInputKind.Visibility))
                errors.Add($"{at} binds a visibility slot but is not a hide edit");

            if (bindings.Count == 0) errors.Add($"{at} has no target slots");
            if (!hide)
            {
                var requiredRoutes = new List<TargetSlot>();
                foreach (var slot in targetSlots.Where(slot => slot is not null
                             && slot.Part.SameAs(edit.Target) && slot.Domain == TargetSlotDomain.Game
                             && slot.Input != TargetInputKind.Visibility))
                    if (!requiredRoutes.Any(route => route.SameRoute(slot))) requiredRoutes.Add(slot);
                foreach (var route in requiredRoutes.Where(route =>
                             !boundSlots.Any(slot => slot.SameRoute(route))))
                    errors.Add($"{at} has no binding for target route represented by slot '{route.Id}'");
            }
        }

        ValidatePlacements(project.Always, edits, "always", contentLimit: false, errors);
        ValidateKeyGroups(project, edits, errors);

        return errors;
    }

    /// <summary>Non-blocking authored observations. Build surfaces carry these beside the settled plan.</summary>
    public static IReadOnlyList<string> Warnings(AuthoredProject project)
    {
        var warnings = new List<string>();
        foreach (var group in (project.KeyGroups ?? new List<KeyGroup>()).Where(group => group is not null))
        {
            var states = (group.States ?? new List<KeyGroupState>()).Where(state => state is not null).ToList();
            if (states.Count < 2) continue;
            string signature = StateSignature(states[0]);
            if (states.Skip(1).All(state => string.Equals(signature, StateSignature(state),
                    StringComparison.Ordinal)))
                warnings.Add($"{PlacementNames.Group(group)} switches nothing.");
        }
        return warnings;
    }

    private static void ValidateKeyGroups(AuthoredProject project,
        IReadOnlyDictionary<string, EditDefinition> edits, List<string> errors)
    {
        Unique(project.KeyGroups, g => g.Id, "key group", errors);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in (project.KeyGroups ?? new List<KeyGroup>()).Where(g => g is not null))
        {
            string at = $"key group '{group.Id}'";
            if (group.Key is { } key)
            {
                if (ModKeys.Normalize(key) is not { } normalized
                    || !string.Equals(normalized, key, StringComparison.Ordinal))
                    errors.Add($"{at} has an invalid key");
                else if (!keys.TryAdd(normalized, group.Id))
                    errors.Add($"{at} shares key '{normalized}' with key group '{keys[normalized]}'");
            }

            var states = (group.States ?? new List<KeyGroupState>()).Where(s => s is not null).ToList();
            if (states.Count < 2)
                errors.Add($"{at} has fewer than two states; delete the group instead");
            var stateIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < states.Count; i++)
            {
                string sat = $"{at} state {i}";
                if (string.IsNullOrWhiteSpace(states[i].Id)) errors.Add($"{sat} has no id");
                else if (!stateIds.Add(states[i].Id))
                    errors.Add($"{at} has duplicate state id '{states[i].Id}'");
                ValidatePlacements(states[i].ActiveEditIds, edits, sat, contentLimit: true, errors);
            }
        }
    }

    private static void ValidatePlacements(IReadOnlyList<string>? placements,
        IReadOnlyDictionary<string, EditDefinition> edits, string at, bool contentLimit,
        List<string> errors)
    {
        if (placements is null)
        {
            errors.Add($"{at} has no active-edit list");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? editId in placements)
        {
            if (string.IsNullOrWhiteSpace(editId))
            {
                errors.Add($"{at} has a placement with no edit id");
                continue;
            }
            if (!seen.Add(editId)) errors.Add($"{at} places edit definition '{editId}' more than once");
            if (!edits.TryGetValue(editId, out var edit))
            {
                errors.Add($"{at} names missing edit definition '{editId}'");
                continue;
            }
            if (!contentLimit || edit.Kind != EditDefinitionKind.Content || edit.Target is null) continue;
            if (content.TryGetValue(edit.Target.Key, out string? other))
                errors.Add($"{at} places content edits '{other}' and '{edit.Id}' for part "
                    + $"'{edit.Target.RendererSlot}'");
            else content.Add(edit.Target.Key, edit.Id);
        }
    }

    internal static string StateSignature(KeyGroupState state) => string.Join('\x1e',
        (state.ActiveEditIds ?? new List<string>()).OrderBy(id => id, StringComparer.Ordinal));

    private static Dictionary<string, T> Unique<T>(IEnumerable<T>? items, Func<T, string> id,
        string label, List<string> errors) where T : class
    {
        var found = new Dictionary<string, T>(StringComparer.Ordinal);
        if (items is null) { errors.Add($"{label} list is required"); return found; }
        foreach (var item in items)
        {
            if (item is null) { errors.Add($"{label} entry is required"); continue; }
            string key = id(item);
            if (string.IsNullOrWhiteSpace(key)) { errors.Add($"{label} id is required"); continue; }
            if (!found.TryAdd(key, item)) errors.Add($"duplicate {label} id '{key}'");
        }
        return found;
    }

    private static void ValidateBinding(Binding binding, TargetSlot slot,
        IReadOnlyDictionary<string, ProjectAsset> assets, IReadOnlyDictionary<string, TargetSlot> slots,
        IReadOnlyDictionary<string, EditDefinition> edits, string at, List<string> errors)
    {
        bool hasAsset = binding.ProjectAssetId is not null;
        bool hasSource = binding.SourceSlot is not null;
        if (binding.Kind == BindingKind.Unknown) errors.Add($"{at} has no binding kind");
        if (binding.Kind == BindingKind.ProjectAsset)
        {
            if (!hasAsset || !assets.TryGetValue(binding.ProjectAssetId!, out var asset))
                errors.Add($"{at} names a missing project asset");
            else if (!Compatible(asset.Kind, slot.Input))
                errors.Add($"{at} binds {asset.Kind} to {slot.Input}");
            else if (slot.Input == TargetInputKind.MaterialValue
                && !string.Equals(asset.Value?.Semantic, slot.Semantic, StringComparison.Ordinal))
                errors.Add($"{at} binds another material-value semantic");
            if (hasSource) errors.Add($"{at} carries a source slot with a project asset");
        }
        else if (binding.Kind == BindingKind.SourceSlot)
        {
            if (!hasSource || string.IsNullOrWhiteSpace(binding.SourceSlot!.SlotId)
                || !slots.TryGetValue(binding.SourceSlot.SlotId, out var source))
                errors.Add($"{at} names a missing source slot");
            else
            {
                if (source.Input != slot.Input) errors.Add($"{at} source slot has another input kind");
                if (slot.Input == TargetInputKind.MaterialValue
                    && !string.Equals(source.Semantic, slot.Semantic, StringComparison.Ordinal))
                    errors.Add($"{at} source slot has another material-value semantic");
                // A source that names no edit is asking the installed game, and what addresses the game is
                // the slot's domain: a save files a part's game slots under the edit that answers it, so
                // asking who holds one would refuse a stock pick on every project from its first save on.
                if (binding.SourceSlot.EditDefinitionId is null && source.Domain != TargetSlotDomain.Game)
                    errors.Add($"{at} names an edit-output source slot as a game slot");
                if (binding.SourceSlot.EditDefinitionId is { } sourceEdit)
                {
                    if (!edits.TryGetValue(sourceEdit, out var namedEdit))
                        errors.Add($"{at} names a missing source edit");
                    else if (!namedEdit.Bindings.Any(candidate =>
                                 string.Equals(candidate.SlotId, source.Id, StringComparison.Ordinal)))
                        errors.Add($"{at} source slot is not bound by its named edit");
                }
            }
            if (hasAsset) errors.Add($"{at} carries a project asset with a source slot");
        }
        else if (hasAsset || hasSource) errors.Add($"{at} carries a payload its binding kind does not use");

        // Domain says whether there is an installed game value to request. Edit outputs have none.
        if (binding.Kind == BindingKind.TargetGameValue && slot.Domain != TargetSlotDomain.Game)
            errors.Add($"{at} asks an edit-output slot for a target game value");
        if (binding.Kind == BindingKind.Neutral && slot.Input is not (TargetInputKind.Normal or TargetInputKind.Rmo))
            errors.Add($"{at} asks an input with no neutral value to use neutral");
        if (binding.Kind == BindingKind.Hidden && slot.Input is not (TargetInputKind.Geometry or TargetInputKind.Visibility))
            errors.Add($"{at} hides a non-renderable input");
    }

    private static bool Compatible(ProjectAssetKind asset, TargetInputKind input) => (asset, input) switch
    {
        (ProjectAssetKind.Geometry, TargetInputKind.Geometry) => true,
        (ProjectAssetKind.Picture, TargetInputKind.BaseColor or TargetInputKind.Normal
            or TargetInputKind.Rmo or TargetInputKind.Blend or TargetInputKind.Texture) => true,
        (ProjectAssetKind.Ramp, TargetInputKind.Ramp) => true,
        (ProjectAssetKind.StructuredValue, TargetInputKind.RmoAlpha or TargetInputKind.MaterialValue) => true,
        _ => false,
    };

    private static bool NeedsMaterial(TargetInputKind input) => input is
        TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
        or TargetInputKind.Blend or TargetInputKind.Texture or TargetInputKind.RmoAlpha or TargetInputKind.Ramp
        or TargetInputKind.MaterialValue;

    private static void ValidateGameRef(GameAssetRef? value, string at, List<string> errors)
    {
        if (value is null) { errors.Add($"{at} is required"); return; }
        if (string.IsNullOrWhiteSpace(value.GameBuild)) errors.Add($"{at} has no game build");
        if (string.IsNullOrWhiteSpace(value.LogicalBundle)) errors.Add($"{at} has no logical bundle");
        if (value.PathId == 0) errors.Add($"{at} has no path id");
    }

    private static void ValidatePart(TargetPart? part, string at, List<string> errors)
    {
        if (part is null) { errors.Add($"{at} has no target part"); return; }
        if (string.IsNullOrWhiteSpace(part.Subject)) errors.Add($"{at} has no subject");
        if (string.IsNullOrWhiteSpace(part.Outfit)) errors.Add($"{at} has no outfit");
        if (string.IsNullOrWhiteSpace(part.RendererSlot)) errors.Add($"{at} has no renderer slot");
    }

    internal static bool IsProjectRelativeFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return !path.Replace('\\', '/').Split('/').Any(p => p is "" or "." or "..");
    }

    private static void ValidateLineageCycles(IEnumerable<ProjectAsset> all,
        IReadOnlyDictionary<string, ProjectAsset> assets, List<string> errors)
    {
        foreach (var start in all)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ProjectAsset? current = start;
            while (current?.Source?.ProjectAssetId is { } next && assets.TryGetValue(next, out current))
                if (!seen.Add(next)) { errors.Add($"project asset '{start.Id}' has cyclic lineage"); break; }
        }
    }
}

/// <summary>Schema-aware JSON persistence for authored intent. It only overwrites another schema-2
/// manifest, so loading a released project can never make a later save silently replace it.</summary>
public static class AuthoredProjectSerializer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    public static string Serialize(AuthoredProject project)
    {
        AuthoredWorkspaceNormalizer.Normalize(project);
        ThrowIfInvalid(project);
        return JsonSerializer.Serialize(project, Json);
    }

    /// <summary>What an open says about a project file it cannot read at all. The three ways a file fails
    /// that read — not JSON, no version in it, nothing in it — are one thing to the person opening it, and
    /// none of them has an action in this app.</summary>
    public const string DamagedProject = "This mod's project file is damaged and cannot be read.";

    /// <summary>What an open says about a project file written by a version this one does not read. Said by
    /// app version rather than by the number in the file, which names nothing the modder can act on.</summary>
    public const string NewerProject =
        "This mod was made with a newer version of Doll Remolding Lab. Update the app to open it.";

    /// <summary>What an open says when the folder holds no project file. The path is named because the
    /// modder chose it, and the recent-mods list adds its own line when the whole folder is gone.</summary>
    public static string MissingProject(string file) => $"There is no project file at {file}.";

    public static AuthoredProject Deserialize(string json)
    {
        int schema = ReadSchema(json);
        if (schema != AuthoredProject.CurrentSchema)
            throw new InvalidDataException(schema > AuthoredProject.CurrentSchema
                ? NewerProject : DamagedProject);

        AuthoredProject? project;
        try { project = JsonSerializer.Deserialize<AuthoredProject>(json, Json); }
        catch (JsonException e) { throw new InvalidDataException(DamagedProject, e); }
        if (project is null) throw new InvalidDataException(DamagedProject);
        AuthoredWorkspaceNormalizer.Normalize(project);
        ThrowIfInvalid(project);
        return project;
    }

    /// <summary>Detach one workspace index from whoever handed it over. The Edit-side session takes the
    /// inventory by value like everything else it holds, so a caller still writing to its own copy cannot
    /// reach inside a committed transaction.</summary>
    internal static string SerializeWorkspaceIndex(AuthoredWorkspaceIndex index) =>
        JsonSerializer.Serialize(index, Json);

    /// <inheritdoc cref="SerializeWorkspaceIndex"/>
    internal static AuthoredWorkspaceIndex DeserializeWorkspaceIndex(string json) =>
        JsonSerializer.Deserialize<AuthoredWorkspaceIndex>(json, Json)
        ?? throw new InvalidDataException("workspace index is empty");

    public static AuthoredProject Load(string path)
    {
        string file = Directory.Exists(path) ? ModProject.ManifestPathFor(path) : path;
        if (!File.Exists(file)) throw new FileNotFoundException(MissingProject(file), file);
        var project = Deserialize(File.ReadAllText(file));
        project.RootDir = Path.GetDirectoryName(Path.GetFullPath(file));
        return project;
    }

    /// <summary>Write a new schema-2 manifest or update an existing schema-2 manifest atomically. A
    /// schema-1 file requires the explicit migration save route that consumes a migration report.</summary>
    public static void Save(AuthoredProject project, string path)
    {
        string file = Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar)
            || !path.EndsWith(ModProject.FileName, StringComparison.OrdinalIgnoreCase)
            ? ModProject.ManifestPathFor(path)
            : path;
        string json = Serialize(project);
        if (File.Exists(file) && ReadSchema(File.ReadAllText(file)) != AuthoredProject.CurrentSchema)
            throw new InvalidOperationException("authored project save refuses to overwrite a non-schema-2 manifest");

        string dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(dir);
        AtomicWrite(file, json, backup: false);
        project.RootDir = dir;
    }

    /// <summary>Atomically replace a released schema-1 manifest with the authored intent that supersedes it.
    /// The outgoing manifest is retained as <c>mod.drlproj.bak</c>.
    ///
    /// <para>Whether the conversion was faithful enough to write at all is settled where the conversion
    /// happens — a blocked adaptation never becomes an edit session, so no such project reaches here.</para>
    /// </summary>
    public static void SaveMigrated(AuthoredProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        string file = ProjectFile(path);
        string json = Serialize(project);
        string dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(dir);
        AtomicWrite(file, json, backup: true);
        project.RootDir = dir;
    }

    internal static int ReadSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("schema", out var schema)
                || !schema.TryGetInt32(out int value))
                throw new InvalidDataException(DamagedProject);
            return value;
        }
        catch (JsonException e) { throw new InvalidDataException(DamagedProject, e); }
    }

    /// <summary>Refuse a project the validator will not stand behind, saying the one thing an open or a save
    /// can say about it. The validator's own list is per FIELD — a slot id, a binding kind, a placement that
    /// names nothing — and none of it is a sentence for the person holding the mod, so it rides an inner
    /// exception into the log the surface writes rather than onto the surface itself.</summary>
    private static void ThrowIfInvalid(AuthoredProject project)
    {
        var errors = AuthoredProjectValidator.Errors(project);
        if (errors.Count > 0)
            throw new InvalidDataException(DamagedProject, new InvalidDataException(
                "authored project is invalid: " + string.Join("; ", errors)));
    }

    private static void AtomicWrite(string file, string json, bool backup)
    {
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(tmp, file, backup ? file + ".bak" : null,
            ignoreMetadataErrors: true);
        else File.Move(tmp, file);
    }

    /// <summary>Read only the manifest version so the application can select released compatibility or
    /// authored loading without deserializing one shape as the other.</summary>
    public static int SchemaOf(string path)
    {
        string file = Directory.Exists(path) ? ModProject.ManifestPathFor(path) : path;
        if (!File.Exists(file)) throw new FileNotFoundException(MissingProject(file), file);
        return ReadSchema(File.ReadAllText(file));
    }

    private static string ProjectFile(string path) =>
        Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar)
            || !path.EndsWith(ModProject.FileName, StringComparison.OrdinalIgnoreCase)
            ? ModProject.ManifestPathFor(path)
            : path;
}
