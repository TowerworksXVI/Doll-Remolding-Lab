using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remold.Core.Textures;

namespace Remold.Core.Project;

/// <summary>The one mutable Edit-side owner of schema-2 intent. Each command is applied to a private copy
/// and committed only after the complete authored project validates, so a failed chooser, fork or
/// material-source batch cannot leave half-written intent behind.</summary>
public sealed partial class AuthoredEditSession
{
    public const string DefaultOptionId = "default";

    private readonly object _gate = new();
    private AuthoredProject _project;
    private long _revision;
    private bool _inTransaction;

    public event EventHandler<AuthoredProjectChangedEventArgs>? Changed;

    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

    public AuthoredEditSession(AuthoredProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        AuthoredWorkspaceNormalizer.Normalize(project);
        var errors = AuthoredProjectValidator.Errors(project);
        if (errors.Count > 0)
            throw new InvalidDataException("authored edit session requires valid intent: "
                + string.Join("; ", errors));
        _project = Clone(project);
    }

    /// <summary>A detached snapshot. Callers can inspect it without acquiring ownership of the live model.</summary>
    public AuthoredProject Snapshot()
    {
        lock (_gate) return Clone(_project);
    }

    /// <summary>Point the model at the folder it lives in. The root is not part of the manifest — it is
    /// where the manifest was found — so it is set on the live model directly rather than committed as
    /// intent through a validating change.</summary>
    public void SetRootDir(string? root)
    {
        lock (_gate)
            if (string.Equals(_project.RootDir, root, StringComparison.OrdinalIgnoreCase)) return;
        Change(project => project.RootDir = root);
    }

    /// <summary>The compatibility reading of one part's Always placements.</summary>
    public PartEditState Part(TargetPart target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            var edit = _project.Always.Select(id => _project.EditDefinitions.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal)))
                .FirstOrDefault(candidate => candidate is not null && candidate.Target.SameAs(target));
            return new PartEditState(Clone(target), edit is null ? CompositionState.Vanilla
                : edit.Kind == EditDefinitionKind.Hide ? CompositionState.Hidden : CompositionState.Edit,
                edit?.Id);
        }
    }

    /// <summary>Every explicit slot answer in one edit, paired with the project asset where it has one.</summary>
    public IReadOnlyList<EditSlotState> Slots(string editDefinitionId)
    {
        lock (_gate) return Slots(_project, editDefinitionId);
    }

    private static IReadOnlyList<EditSlotState> Slots(AuthoredProject project, string editDefinitionId)
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var slots = project.TargetSlots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var assets = project.ProjectAssets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        return edit.Bindings.Select(binding =>
        {
            var slot = slots[binding.SlotId];
            var asset = binding.ProjectAssetId is { } assetId ? assets.GetValueOrDefault(assetId) : null;
            return new EditSlotState(Clone(slot), Clone(binding), asset is null ? null : Clone(asset));
        }).ToList();
    }

    /// <summary>Write the identity form the Build page holds: what the mod is called, who by, what it says
    /// of itself, and the mod-level toggle. The subject labels come with it — they are the same form's
    /// answer for which subject the package names itself after. Everything else the info block carries, the
    /// preview picture above all, is untouched: a form that does not show a field never clears it.</summary>
    public void SetIdentity(string name, string version, string? author, string? description,
        string? toggleKey, bool includeRepairData, string? character, string? outfit,
        bool persistToggleKey = false) => Change(project =>
    {
        project.Info.Name = name;
        project.Info.Version = version;
        project.Info.Author = author;
        project.Info.Description = description;
        project.Info.ToggleKey = toggleKey;
        project.Info.PersistToggleKey = persistToggleKey;
        project.Info.IncludeRepairData = includeRepairData;
        project.Info.Character = character;
        project.Info.Outfit = outfit;
    });

    /// <summary>Rename the mod on its own, which is what Save As does before it writes a copy.</summary>
    public void SetName(string name) => Change(project => project.Info.Name = name);

    /// <summary>Point the mod at its preview picture, or take it away.</summary>
    public void SetPreview(string? projectRelativeFile) => Change(project =>
        project.Info.Preview = string.IsNullOrWhiteSpace(projectRelativeFile)
            ? null : NormalizeFile(projectRelativeFile));

    /// <summary>Stamp which catalog the project was last authored against. A null stamp is no stamp: the
    /// policy that decides what to carry forward lives in <see cref="AuthoredAgainstPolicy"/>, and this
    /// records the answer it gave.</summary>
    public void SetAuthoredAgainst(string? catalogVersion) => Change(project =>
        project.AuthoredAgainst = catalogVersion is null
            ? null : new AuthoredAgainst { CatalogVersion = catalogVersion });

    /// <summary>Take the workspace inventory the current workbench cannot rediscover without reading the
    /// game — which subjects are in the mod, and the materialized targets under them. It is inventory, not
    /// intent: what is edited, hidden, keyed, excluded or picked is the authored model's answer and is
    /// stripped on the way in, so a captured index can never smuggle a second opinion about any of it.</summary>
    public void SetWorkspaceIndex(AuthoredWorkspaceIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        string json = AuthoredProjectSerializer.SerializeWorkspaceIndex(index);
        Change(project => SetWorkspaceIndex(project,
            AuthoredProjectSerializer.DeserializeWorkspaceIndex(json)));
    }

    private static void SetWorkspaceIndex(AuthoredProject project, AuthoredWorkspaceIndex index)
    {
        project.WorkspaceIndex = index;
        AuthoredWorkspaceNormalizer.Normalize(project);
    }

    /// <summary>Where a toon ramp picked on one of a part's installed materials goes: the game-domain ramp
    /// slot at that material position. A pick addresses the material the game draws with, so it is a game
    /// slot and never one of a replacement's own outputs, which a replacement's own ramp answers for.
    ///
    /// <para>A material whose resolved shader binds no ramp input has no such slot —
    /// <see cref="EnsurePartSlots"/> mints one input per input the install answers for, and mints none
    /// where the material carries none. That is refused by name rather than by minting a place the game
    /// would never read: a ramp bound where the shader has no ramp is a value that could not draw.</para>
    /// </summary>
    public string GameRampSlot(TargetPart target, int materialSlotIndex)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            var slots = _project.TargetSlots.Where(slot => slot.Part.SameAs(target)
                && slot.Domain == TargetSlotDomain.Game && slot.Input == TargetInputKind.Ramp
                && slot.MaterialSlotIndex == materialSlotIndex)
                .OrderBy(slot => slot.Id, StringComparer.Ordinal).ToList();
            if (slots.Count == 0)
                throw new AuthoredRefusalException(
                    $"Material {materialSlotIndex} draws without a toon ramp, so one cannot be set here.");
            return slots[0].Id;
        }
    }

    /// <summary>Give the project the game slots of one part it has never touched, read from the current
    /// install through the same resolved identity schema-1 adaptation and build planning use. Until something mints them a
    /// part has no exact places to bind, so there is nothing to build an edit from and nothing for a hide to
    /// re-anchor on; this is the command every route onto a fresh part goes through first.
    ///
    /// <para>The shape is shared with schema-1 adaptation and the emission projection: lod0 geometry, one geometry slot per
    /// LOD tier, and each material's supported texture inputs at its own submesh and material-slot position.
    /// A part opened here and a part the adapter slotted are the same thing to the validator, the planner
    /// and the projection, so nothing downstream can tell which route a slot arrived by. Visibility is not
    /// among them: a hide owns its own visibility slot, and a state's hidden answer re-anchors on what the
    /// part already has.</para>
    ///
    /// <para>Adding only what is missing, judged by what a slot addresses rather than who holds it, so a part
    /// is never given a second slot on one game object. A game input the part's existing content edits have
    /// no answer for is one they now owe one, and the answer is the game's own value — an edit says what it
    /// asks of every slot its result touches, and absence is never read as "probably the game's". An install
    /// that cannot name an exact object for a route is refused rather than half-opening the part.</para>
    /// </summary>
    public void EnsurePartSlots(TargetPart target, Func<TargetPart, LegacyResolvedPart?> resolvePart)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolvePart);
        var resolved = resolvePart(Clone(target))
            ?? throw new AuthoredRefusalException(PartNotInstalled);
        Change(project => EnsurePartSlots(project, target, resolved));
        // Draw shape is live-install evidence, deliberately absent from JSON. An already-slotted project
        // can therefore have no serialized change for this read; refresh the committed project's transient
        // anchor after the transaction so the next replacement publish can still fold by what was read.
        lock (_gate) RefreshMaterialMeasurements(_project, target, resolved);
    }

    /// <summary>The shared mutation behind a session open and schema-1 adaptation.</summary>
    internal static void EnsurePartSlots(AuthoredProject project, TargetPart target,
        LegacyResolvedPart resolved)
    {
        var slots = GameSlots(target, resolved);
        foreach (var stale in project.TargetSlots.Where(candidate => candidate.Part.SameAs(target)
                     && candidate.Domain == TargetSlotDomain.Game && IsTextureInput(candidate.Input)))
            stale.MaterialBindingPresent = false;
        var answering = project.EditDefinitions.Where(edit => edit.Kind == EditDefinitionKind.Content
            && edit.Target.SameAs(target)).ToList();
        var taken = project.TargetSlots.Select(slot => slot.Id).ToList();
        foreach (var slot in slots)
        {
            var existing = project.TargetSlots.Where(candidate => candidate.SameRoute(slot)).ToList();
            if (existing.Count > 0)
            {
                // Pre-property schema-2 rows used the known input as their whole identity. The installed
                // answer enriches that row in place; after the first enrichment a second property of the
                // same coarse kind no longer matches it and receives its own slot below.
                foreach (var matched in existing)
                {
                    if (string.IsNullOrWhiteSpace(matched.ShaderProperty)
                        && !string.IsNullOrWhiteSpace(slot.ShaderProperty))
                        matched.ShaderProperty = slot.ShaderProperty;
                    matched.DrawIndexCount = slot.DrawIndexCount;
                    matched.MaterialIndexCounts = slot.MaterialIndexCounts?.ToArray();
                    matched.MaterialBindingPresent = true;
                }
                continue;
            }
            var minted = Clone(slot);
            minted.Id = MintId("slot", taken);
            taken.Add(minted.Id);
            project.TargetSlots.Add(minted);
            foreach (var edit in answering)
                edit.Bindings.Add(new Binding
                {
                    SlotId = minted.Id,
                    Kind = BindingKind.TargetGameValue,
                });
        }
    }

    private static void RefreshMaterialMeasurements(AuthoredProject project, TargetPart target,
        LegacyResolvedPart resolved)
    {
        var counts = resolved.MaterialIndexCounts;
        var current = GameSlots(target, resolved).Where(slot => IsTextureInput(slot.Input)).ToList();
        foreach (var slot in project.TargetSlots.Where(slot => slot.Part.SameAs(target)))
        {
            if (slot.Input == TargetInputKind.Geometry && (slot.Tier is null
                    || string.Equals(slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase)))
                slot.MaterialIndexCounts = counts?.ToArray();
            if (slot.MaterialSlotIndex is { } position)
                slot.DrawIndexCount = counts is not null && position >= 0 && position < counts.Count
                    ? counts[position] : counts is null ? null : 0;
            if (slot.Domain == TargetSlotDomain.Game && IsTextureInput(slot.Input))
                slot.MaterialBindingPresent = current.Any(candidate => candidate.SameRoute(slot));
        }
    }

    /// <summary>The game-domain slot one shading value of one material position lives at, minted on
    /// first use. Material-value slots are minted LAZILY, per semantic actually authored or asked
    /// about — <see cref="EnsurePartSlots"/> deliberately does not fan a part out into one slot per
    /// authorable field, and a part whose values nobody touches carries none. Minting resolves the
    /// exact current material the same way every other game slot is minted, and gives every content
    /// edit answering the part its game-value default, so absence is never read as an answer.</summary>
    public string EnsureMaterialValueSlot(TargetPart target, int materialSlotIndex, string semantic,
        Func<TargetPart, LegacyResolvedPart?> resolvePart)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolvePart);
        if (MaterialValueCatalog.Field(semantic) is null)
            throw new ArgumentException($"'{semantic}' is not an authorable shading value",
                nameof(semantic));
        lock (_gate)
        {
            var existing = _project.TargetSlots.FirstOrDefault(slot => slot.Part.SameAs(target)
                && slot.Domain == TargetSlotDomain.Game
                && slot.Input == TargetInputKind.MaterialValue
                && slot.MaterialSlotIndex == materialSlotIndex
                && string.Equals(slot.Semantic, semantic, StringComparison.Ordinal));
            if (existing is not null) return existing.Id;
        }
        var resolved = resolvePart(Clone(target))
            ?? throw new AuthoredRefusalException(PartNotInstalled);
        var material = (resolved.Materials ?? Array.Empty<LegacyResolvedMaterial>())
            .FirstOrDefault(candidate => candidate.MaterialSlotIndex == materialSlotIndex)
            ?? throw new AuthoredRefusalException($"This part has no material {materialSlotIndex}.");
        RequireExact(material.Material, $"material {materialSlotIndex}");
        RequireExact(resolved.Renderer, "");
        RequireExact(resolved.Mesh, "mesh");
        string minted = "";
        Change(project =>
        {
            minted = EnsureMaterialValueSlot(project, target, materialSlotIndex, semantic,
                resolved, material);
        });
        return minted;
    }

    private static string EnsureMaterialValueSlot(AuthoredProject project, TargetPart target,
        int materialSlotIndex, string semantic, LegacyResolvedPart resolved,
        LegacyResolvedMaterial? knownMaterial = null)
    {
        var existing = MaterialValueSlot(project, target, materialSlotIndex, semantic);
        if (existing is not null) return existing.Id;
        var material = knownMaterial ?? (resolved.Materials ?? Array.Empty<LegacyResolvedMaterial>())
            .FirstOrDefault(candidate => candidate.MaterialSlotIndex == materialSlotIndex)
            ?? throw new AuthoredRefusalException($"This part has no material {materialSlotIndex}.");
        RequireExact(material.Material, $"material {materialSlotIndex}");
        RequireExact(resolved.Renderer, "");
        RequireExact(resolved.Mesh, "mesh");
        string id = MintId("slot", project.TargetSlots.Select(slot => slot.Id));
        project.TargetSlots.Add(new TargetSlot
        {
            Id = id,
            Part = Clone(target),
            SubmeshIndex = materialSlotIndex,
            MaterialSlotIndex = materialSlotIndex,
            Input = TargetInputKind.MaterialValue,
            Semantic = semantic,
            Renderer = Clone(resolved.Renderer),
            Mesh = Clone(resolved.Mesh),
            Material = Clone(material.Material),
        });
        foreach (var edit in project.EditDefinitions.Where(edit =>
                     edit.Kind == EditDefinitionKind.Content && edit.Target.SameAs(target)))
            edit.Bindings.Add(new Binding { SlotId = id, Kind = BindingKind.TargetGameValue });
        return id;
    }

    private static TargetSlot? MaterialValueSlot(AuthoredProject project, TargetPart target,
        int materialSlotIndex, string semantic) => project.TargetSlots.FirstOrDefault(slot =>
        slot.Part.SameAs(target) && slot.Domain == TargetSlotDomain.Game
        && slot.Input == TargetInputKind.MaterialValue
        && slot.MaterialSlotIndex == materialSlotIndex
        && string.Equals(slot.Semantic, semantic, StringComparison.Ordinal));

    /// <summary>One part's game-domain slots as the install answers for them, in the shape and order the
    /// schema-1 adapter and emission projection use: lod0 geometry, then each LOD tier, then each material's supported
    /// texture bindings. The exact shader property, not the coarse semantic, separates bindings at one
    /// material position; two properties may intentionally point at the same Texture2D resource.</summary>
    private static List<TargetSlot> GameSlots(TargetPart target, LegacyResolvedPart resolved)
    {
        var slots = new List<TargetSlot>();
        RequireExact(resolved.Renderer, "");
        RequireExact(resolved.Mesh, "mesh");
        slots.Add(new TargetSlot
        {
            Part = Clone(target),
            Tier = "lod0",
            Input = TargetInputKind.Geometry,
            Renderer = Clone(resolved.Renderer),
            Mesh = Clone(resolved.Mesh),
            MaterialIndexCounts = resolved.MaterialIndexCounts?.ToArray(),
        });
        foreach (var tier in resolved.Tiers ?? Array.Empty<LegacyResolvedTier>())
        {
            // The tier's own name is the engine's word for it and is on no screen; what the modder can act
            // on is that the part's lower-detail versions could not be pinned down.
            RequireExact(tier.Renderer, LowerDetailVersions);
            RequireExact(tier.Mesh, LowerDetailVersions);
            slots.Add(new TargetSlot
            {
                Part = Clone(target),
                Tier = tier.Tier,
                Input = TargetInputKind.Geometry,
                Renderer = Clone(tier.Renderer),
                Mesh = Clone(tier.Mesh),
            });
        }
        foreach (var material in resolved.Materials ?? Array.Empty<LegacyResolvedMaterial>())
        {
            var inputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var texture in material.Textures ?? Array.Empty<LegacyResolvedTexture>())
            {
                if (!IsTextureInput(texture.Input)) continue;
                if (texture.Input == TargetInputKind.Texture
                    && string.IsNullOrWhiteSpace(texture.ShaderProperty))
                    throw new AuthoredRefusalException($"Material {material.MaterialSlotIndex} uses a "
                        + "texture this app cannot name, so the part cannot be opened.");
                string identity = !string.IsNullOrWhiteSpace(texture.ShaderProperty)
                    ? texture.ShaderProperty!
                    : "\u001f" + texture.Input;
                if (!inputs.Add(identity)) continue;
                RequireExact(material.Material, $"material {material.MaterialSlotIndex}");
                slots.Add(new TargetSlot
                {
                    Part = Clone(target),
                    SubmeshIndex = material.MaterialSlotIndex,
                    MaterialSlotIndex = material.MaterialSlotIndex,
                    Input = texture.Input,
                    ShaderProperty = texture.ShaderProperty,
                    Renderer = Clone(resolved.Renderer),
                    Mesh = Clone(resolved.Mesh),
                    Material = Clone(material.Material),
                    MaterialBindingPresent = true,
                    DrawIndexCount = resolved.MaterialIndexCounts is { } counts
                        && material.MaterialSlotIndex >= 0
                        && material.MaterialSlotIndex < counts.Count
                            ? counts[material.MaterialSlotIndex]
                            : resolved.MaterialIndexCounts is null ? null : 0,
                });
            }
        }
        return slots;
    }

    private static bool IsTextureInput(TargetInputKind input) => input is
        TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
        or TargetInputKind.Blend or TargetInputKind.Ramp or TargetInputKind.Texture;

    /// <summary>What a command says when the current game files do not have the part it was asked to record
    /// against. The sentence names the state rather than the address the model finds the part by: every
    /// surface it reaches already stands on the part — the row it is kept on, and the status line after a
    /// click on that row.</summary>
    internal const string PartNotInstalled =
        "This part isn't in the current game files, so there is nowhere to record its values.";

    /// <summary>The same gap on the other side of a copy, where the values are read rather than
    /// recorded.</summary>
    internal const string SourcePartNotInstalled =
        "The part these values come from isn't in the current game files, so they cannot be read.";

    /// <summary>What a command says when the project holds no places for the part at all — nothing has
    /// opened it, and the game files could not answer for it either. <paramref name="what"/> is what was
    /// being added, in the modder's own words.</summary>
    internal static string NowhereToRecord(string what) =>
        $"This part has nowhere to record {what}. Load the game files, then try again.";

    /// <summary>Refuse a route the install cannot name one object for. The adapter reports the same gap as
    /// blocking migration; here there is no report to carry it, so it is said out loud instead of minting a
    /// slot nothing can be bound at.</summary>
    /// <summary>What the part's LOD tiers are called on screen. One phrase for the whole set: which tier is
    /// missing is a distinction nobody can act on, and the tier's own name is the engine's.</summary>
    private const string LowerDetailVersions = "lower-detail versions";

    /// <param name="what">Which of the part's things is missing, in the modder's own words. Empty where the
    /// part itself is what the game files cannot pin down.</param>
    private static void RequireExact(GameAssetRef value, string what)
    {
        if (string.IsNullOrWhiteSpace(value.GameBuild) || string.IsNullOrWhiteSpace(value.LogicalBundle)
            || value.PathId == 0)
            throw new AuthoredRefusalException(what.Length == 0
                ? "Couldn't find this part in the current game files."
                : $"Couldn't find this part's {what} in the current game files.");
    }

    /// <summary>Add one content edit to a part, fresh from vanilla: every game slot the part addresses asks
    /// the game for its own value, so the new edit is a starting point rather than a change. A part that had
    /// no active content placement at all takes the new edit into Always; every later edit stays in the
    /// library until a placement assigns it.
    ///
    /// <para>Which slots those are is asked of their domain and structural route. The new edit answers one
    /// slot per place the part addresses, not one per duplicate slot record, so equivalent routes collapse
    /// to one representative each. A route already bound by another edit is copied so both edits retain
    /// exact independent binding addresses; otherwise the new edit binds the existing record. Material
    /// value game slots stay shared because their bindings, rather than their slot ids, hold each edit's
    /// independent answer.
    /// Visibility is left out: a content edit binds geometry, pictures, ramp and material values, and a hide
    /// is the one edit that answers for whether a part draws at all.</para>
    ///
    /// Returns the new edit definition's id.</summary>
    public string CreateEdit(TargetPart target, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        string id = "";
        Change(project => id = CreateEdit(project, target, label));
        return id;
    }

    /// <summary>The mutation behind <see cref="CreateEdit(TargetPart, string?)"/>, so a compound change can
    /// mint an edit beside everything else it commits.</summary>
    private static string CreateEdit(AuthoredProject project, TargetPart target, string? label)
    {
        string id = MintId("edit", project.EditDefinitions.Select(edit => edit.Id));
        var slots = ContentSlotRoutes(project, target);
        if (slots.Count == 0)
            throw new AuthoredRefusalException(NowhereToRecord("an edit"));
        var copies = MintSlotCopyIds(project, slots.Where(slot =>
                !(slot.Domain == TargetSlotDomain.Game
                    && slot.Input == TargetInputKind.MaterialValue)
                && project.EditDefinitions.Any(edit => edit.Bindings.Any(binding =>
                    string.Equals(binding.SlotId, slot.Id, StringComparison.Ordinal))))
            .Select(slot => slot.Id));
        AddSlotCopies(project, copies);
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = id,
            Kind = EditDefinitionKind.Content,
            Target = Clone(target),
            Label = NewEditLabel(project, target, label),
            Bindings = slots
                .Select(slot => new Binding
                {
                    SlotId = copies.GetValueOrDefault(slot.Id, slot.Id),
                    Kind = BindingKind.TargetGameValue,
                })
                .ToList(),
        });
        if (!HasPlacement(project, target)) project.Always.Add(id);
        return id;
    }

    /// <summary>Copy one content edit into a second answer for the same part. Project assets are shared, not
    /// copied: two edits naming one asset are one file until a fork sends them apart. Slots the source owns
    /// are its own outputs, so the copy is given its own and its bindings are re-pointed at them. Composition
    /// is untouched — a duplicate is a library entry until something selects it.</summary>
    public string DuplicateEdit(string editDefinitionId, string? label = null)
    {
        string id = "";
        Change(project =>
        {
            var source = RequiredEdit(project, editDefinitionId);
            if (source.Kind != EditDefinitionKind.Content)
                throw new AuthoredRefusalException(
                    "This edit hides the part, and a part has only one of those.");
            id = MintId("edit", project.EditDefinitions.Select(edit => edit.Id));
            var slots = project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
            var slotIds = MintSlotCopyIds(project, source.Bindings
                .Where(binding => slots[binding.SlotId].Domain == TargetSlotDomain.EditOutput)
                .Select(binding => binding.SlotId));
            AddSlotCopies(project, slotIds);
            var bindings = source.Bindings.Select(Clone).ToList();
            foreach (var binding in bindings)
            {
                if (slotIds.TryGetValue(binding.SlotId, out string? mapped)) binding.SlotId = mapped;
                if (binding.SourceSlot is not { } from
                    || !string.Equals(from.EditDefinitionId, source.Id, StringComparison.Ordinal))
                    continue;
                from.EditDefinitionId = id;
                if (slotIds.TryGetValue(from.SlotId, out string? mappedSource)) from.SlotId = mappedSource;
            }
            project.EditDefinitions.Add(new EditDefinition
            {
                Id = id,
                Kind = source.Kind,
                Target = Clone(source.Target),
                Label = NewEditLabel(project, source.Target, label),
                Bindings = bindings,
            });
        });
        return id;
    }

    /// <summary>Rename one edit. A blank name restores the default one it would have been given. An explicit
    /// name is refused when a sibling content edit already holds it, under the same trimmed,
    /// case-insensitive identity creation uses.</summary>
    public void RenameEdit(string editDefinitionId, string? label) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        if (string.IsNullOrWhiteSpace(label))
        {
            edit.Label = DefaultEditLabel(project, edit);
            return;
        }
        string requested = label.Trim();
        var collision = project.EditDefinitions.FirstOrDefault(other => other.Kind == EditDefinitionKind.Content
            && !string.Equals(other.Id, edit.Id, StringComparison.Ordinal)
            && other.Target.SameAs(edit.Target)
            && string.Equals(other.Label.Trim(), requested, StringComparison.OrdinalIgnoreCase));
        if (collision is not null)
            throw new AuthoredRefusalException(
                $"“{collision.Label}” is already the name of another edit for this part.");
        edit.Label = requested;
    });

    /// <summary>Store the warning produced by the latest mesh return for one edit. Null clears it. Return
    /// warnings are authored with the edit so the next project save carries them across an app restart.</summary>
    public void SetReturnWarning(string editDefinitionId, string? warning) => Change(project =>
        RequiredEdit(project, editDefinitionId).ReturnWarning = NormalizeReturnWarning(warning));

    /// <summary>Delete one edit, its placements, the slots it owns and their bindings. Project assets it
    /// named are left alone: an asset no binding uses is still the user's file.</summary>
    public void DeleteEdit(string editDefinitionId) => Change(project =>
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var filed = edit.Bindings.Select(binding => binding.SlotId).ToHashSet(StringComparer.Ordinal);
        var exclusive = filed.Where(slotId => !project.EditDefinitions.Any(other =>
                !string.Equals(other.Id, edit.Id, StringComparison.Ordinal)
                && other.Bindings.Any(binding => string.Equals(binding.SlotId, slotId,
                    StringComparison.Ordinal))))
            .ToHashSet(StringComparer.Ordinal);
        var borrower = project.EditDefinitions.FirstOrDefault(other =>
            !string.Equals(other.Id, edit.Id, StringComparison.Ordinal)
            && other.Bindings.Any(binding => binding.SourceSlot is { } from && exclusive.Contains(from.SlotId)));
        if (borrower is not null)
            throw new AuthoredRefusalException($"'{edit.Label}' cannot be deleted while "
                + $"'{borrower.Label}' takes a value from it.");

        project.EditDefinitions.Remove(edit);
        project.TargetSlots.RemoveAll(slot => exclusive.Contains(slot.Id));
        project.Always.RemoveAll(id => string.Equals(id, edit.Id, StringComparison.Ordinal));
        foreach (var state in project.KeyGroups.SelectMany(group => group.States))
            state.ActiveEditIds.RemoveAll(id => string.Equals(id, edit.Id, StringComparison.Ordinal));
    });

    /// <summary>Give a part its hide edit without placing it, for a caller that is about to place it
    /// somewhere exact. The edit is an ordinary object like any other; <see cref="PlaceEdit(string)"/> and
    /// its state overloads decide where it applies, exactly as they do for a content edit. A part that
    /// already has its hide edit is answered with that one.
    ///
    /// <para>Returns the hide edit definition's id.</para></summary>
    public string CreateHideEdit(TargetPart target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string id = "";
        Change(project => id = EnsureHideEdit(project, target).Id);
        return id;
    }

    /// <summary>Give a part its hide edit and activate it the way any first edit of a part is activated: a
    /// part with no answer on the board yet takes this one into Always, and a part that already has one
    /// keeps its hide in the library until a placement assigns it. The rule is <see cref="CreateEdit"/>'s
    /// own, read from the same place, because a hide edit is an ordinary edit.
    ///
    /// <para>Repeating the command on a part whose hide is already active changes nothing.</para></summary>
    public string AddHideEdit(TargetPart target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string id = "";
        Change(project => id = AddHideEdit(project, target));
        return id;
    }

    /// <summary>The mutation behind <see cref="AddHideEdit(TargetPart)"/>, so a return that hides several
    /// parts commits them with everything else it carries.</summary>
    private static string AddHideEdit(AuthoredProject project, TargetPart target)
    {
        bool placed = HasPlacement(project, target);
        string id = EnsureHideEdit(project, target).Id;
        if (!placed) project.Always.Add(id);
        return id;
    }

    /// <summary>The part's one hide edit, minted where it has none. A hide binds visibility on one exact
    /// route, so the slot it takes is derived from the part's own slots rather than invented: geometry first,
    /// an exact object before a nameless one, and the id order after that, which is one answer whichever
    /// command asks for it.</summary>
    private static EditDefinition EnsureHideEdit(AuthoredProject project, TargetPart target)
    {
        var hides = project.EditDefinitions.Where(edit => edit.Target.SameAs(target)
            && edit.Kind == EditDefinitionKind.Hide).ToList();
        // Not a refusal: EnsureHideEdit is the only thing that mints one, and it mints at most one per
        // part. A second is a defect in this file, and has no wording anyone outside it could act on.
        if (hides.Count > 1)
            throw new InvalidOperationException("the part has more than one hide edit");
        var hide = hides.SingleOrDefault();
        if (hide is null)
        {
            var source = project.TargetSlots.Where(slot => slot.Part.SameAs(target))
                .OrderBy(slot => slot.Input == TargetInputKind.Geometry ? 0 : 1)
                .ThenBy(slot => slot.Mesh is null ? 1 : 0)
                .ThenBy(slot => slot.Id, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw new AuthoredRefusalException(NowhereToRecord("a hide"));
            string editId = Id("edit");
            string slotId = Id("slot");
            hide = new EditDefinition
            {
                Id = editId,
                Kind = EditDefinitionKind.Hide,
                Target = Clone(target),
                Label = "Hidden",
                Bindings = new List<Binding>
                {
                    new() { SlotId = slotId, Kind = BindingKind.Hidden },
                },
            };
            project.EditDefinitions.Add(hide);
            project.TargetSlots.Add(new TargetSlot
            {
                Id = slotId,
                Part = Clone(target),
                Tier = source.Tier,
                Input = TargetInputKind.Visibility,
                Domain = TargetSlotDomain.Game,
                Renderer = Clone(source.Renderer),
                Mesh = source.Mesh is null ? null : Clone(source.Mesh),
            });
        }
        return hide;
    }

    /// <summary>Put one existing compatible project asset on one slot.</summary>
    public void ChooseProjectAsset(string editDefinitionId, string slotId, string projectAssetId) =>
        Change(project => SetBinding(project, editDefinitionId,
            new Binding { SlotId = slotId, Kind = BindingKind.ProjectAsset, ProjectAssetId = projectAssetId }));

    /// <summary>Bind one semantic value to one exact structured-value slot. The payload is immutable and
    /// follows the file asset whose interpretation it records; repeating the same answer is a no-op.</summary>
    public void ChooseStructuredValue(string editDefinitionId, string slotId, string label,
        string projectRelativeFile, string semantic, string value, string? sourceProjectAssetId = null)
    {
        Change(project => ChooseStructuredValue(project, editDefinitionId, slotId, label,
            projectRelativeFile, semantic, value, sourceProjectAssetId));
    }

    /// <summary>The mutation behind <see cref="ChooseStructuredValue"/>, so a Blender return's per-submesh
    /// emissive-mask answers commit with the pictures they follow.</summary>
    private static void ChooseStructuredValue(AuthoredProject project, string editDefinitionId,
        string slotId, string label, string projectRelativeFile, string semantic, string value,
        string? sourceProjectAssetId)
    {
        if (string.IsNullOrWhiteSpace(semantic)) throw new ArgumentException("a semantic is required", nameof(semantic));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("a value is required", nameof(value));
        string normalized = NormalizeFile(projectRelativeFile);
        var edit = RequiredEdit(project, editDefinitionId);
        var current = edit.Bindings.SingleOrDefault(binding =>
                string.Equals(binding.SlotId, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"edit '{editDefinitionId}' has no binding for slot '{slotId}'");
        var asset = current.Kind == BindingKind.ProjectAsset && current.ProjectAssetId is { } currentId
            ? project.ProjectAssets.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, currentId, StringComparison.Ordinal)) : null;
        if (asset?.Kind == ProjectAssetKind.StructuredValue
            && string.Equals(asset.Label, label, StringComparison.Ordinal)
            && string.Equals(asset.File, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(asset.Value?.Semantic, semantic, StringComparison.Ordinal)
            && string.Equals(asset.Value?.Value, value, StringComparison.Ordinal)
            && string.Equals(asset.Source?.ProjectAssetId, sourceProjectAssetId, StringComparison.Ordinal))
            return;
        string id = Id("asset");
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = id,
            Kind = ProjectAssetKind.StructuredValue,
            Label = string.IsNullOrWhiteSpace(label) ? semantic : label.Trim(),
            File = normalized,
            Source = sourceProjectAssetId is null ? null
                : new ProjectAssetSource { ProjectAssetId = sourceProjectAssetId },
            Value = new ProjectAssetValue { Semantic = semantic, Value = value },
        });
        SetBinding(project, editDefinitionId,
            new Binding { SlotId = slotId, Kind = BindingKind.ProjectAsset, ProjectAssetId = id });
    }

    /// <summary>Author one typed-in shading value onto one material-value slot: the value is parsed and
    /// canonicalized against its field's shape (one float, or four for a colour row; the GI-flatten flag
    /// is two-state), recorded in a small value file under <c>values/</c>, and bound through the ordinary
    /// structured-value route. A malformed value refuses with the field's shape in the message rather
    /// than recording text the build would refuse later.</summary>
    public void ChooseMaterialValue(string editDefinitionId, string slotId, string value)
    {
        var before = Snapshot();
        var slot = before.TargetSlots.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"no target slot '{slotId}'");
        string semantic = slot.Semantic
            ?? throw new InvalidOperationException($"slot '{slotId}' carries no shading value");
        var field = MaterialValueCatalog.Field(semantic)
            ?? throw new InvalidOperationException($"'{semantic}' is not an authorable shading value");
        if (!MaterialValueBuildSupport.TryValues(semantic, value, out _, out string canonical))
            throw MaterialValueArgument(field, value);
        ChangeWithValueFiles((project, writes) => SetMaterialValue(project, editDefinitionId,
            slotId, field, semantic, canonical, writes));
    }

    /// <summary>Apply one shading dialog's changed rows as one authored transaction. Any slots the rows
    /// need are minted in the same candidate as their bindings and files, so a stale edit or refused value
    /// leaves no partial answer behind.</summary>
    public void ApplyMaterialValues(string editDefinitionId, TargetPart target, int materialSlotIndex,
        IReadOnlyList<AuthoredMaterialValueEdit> edits,
        Func<TargetPart, LegacyResolvedPart?> resolvePart)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(resolvePart);
        var prepared = edits.Select(edit =>
        {
            var field = MaterialValueCatalog.Field(edit.Semantic)
                ?? throw new ArgumentException($"'{edit.Semantic}' is not an authorable shading value",
                    nameof(edits));
            if (string.IsNullOrWhiteSpace(edit.Value)) return (field, Value: (string?)null);
            if (!MaterialValueBuildSupport.TryValues(edit.Semantic, edit.Value, out _,
                    out string canonical))
                throw MaterialValueArgument(field, edit.Value);
            return (field, Value: canonical);
        }).ToList();
        if (prepared.Select(item => item.field.Semantic).Distinct(StringComparer.Ordinal).Count()
            != prepared.Count)
            throw new ArgumentException("a shading field was supplied more than once", nameof(edits));
        bool needsSlot = prepared.Any(item => item.Value is not null);
        LegacyResolvedPart? resolved = needsSlot ? resolvePart(Clone(target)) : null;
        if (needsSlot && resolved is null)
            throw new AuthoredRefusalException(PartNotInstalled);

        ChangeWithValueFiles((project, writes) =>
        {
            RequiredEdit(project, editDefinitionId);
            foreach (var (field, canonical) in prepared)
            {
                var slot = MaterialValueSlot(project, target, materialSlotIndex, field.Semantic);
                if (canonical is null)
                {
                    if (slot is not null)
                        SetBinding(project, editDefinitionId,
                            new Binding { SlotId = slot.Id, Kind = BindingKind.TargetGameValue });
                    continue;
                }
                string slotId = slot?.Id ?? EnsureMaterialValueSlot(project, target,
                    materialSlotIndex, field.Semantic, resolved!);
                SetMaterialValue(project, editDefinitionId, slotId, field, field.Semantic,
                    canonical, writes);
            }
            RemoveUnauthoredMaterialValueSlots(project);
        });
    }

    /// <summary>Bind all selected source-material values in one transaction. Both sides' lazy slots and
    /// every target binding commit together or not at all.</summary>
    public void CopyMaterialValues(string editDefinitionId, TargetPart target, int materialSlotIndex,
        TargetPart source, int sourceMaterialSlotIndex, IReadOnlyList<string> semantics,
        Func<TargetPart, LegacyResolvedPart?> resolvePart)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(resolvePart);
        var fields = semantics.Distinct(StringComparer.Ordinal).Select(semantic =>
            MaterialValueCatalog.Field(semantic)
            ?? throw new ArgumentException($"'{semantic}' is not an authorable shading value",
                nameof(semantics))).ToList();
        var targetResolved = resolvePart(Clone(target))
            ?? throw new AuthoredRefusalException(PartNotInstalled);
        var sourceResolved = resolvePart(Clone(source))
            ?? throw new AuthoredRefusalException(SourcePartNotInstalled);
        Change(project =>
        {
            RequiredEdit(project, editDefinitionId);
            foreach (var field in fields)
            {
                string from = EnsureMaterialValueSlot(project, source, sourceMaterialSlotIndex,
                    field.Semantic, sourceResolved);
                string onto = EnsureMaterialValueSlot(project, target, materialSlotIndex,
                    field.Semantic, targetResolved);
                SetBinding(project, editDefinitionId, new Binding
                {
                    SlotId = onto,
                    Kind = BindingKind.SourceSlot,
                    SourceSlot = new BindingSourceSlot { SlotId = from },
                });
            }
        });
    }

    public void ChooseTargetGameValue(string editDefinitionId, string slotId) => Change(project =>
    {
        SetBinding(project, editDefinitionId,
            new Binding { SlotId = slotId, Kind = BindingKind.TargetGameValue });
        RemoveUnauthoredMaterialValueSlots(project);
    });

    public void ChooseInheritedCarrier(string editDefinitionId, string slotId) =>
        Choose(editDefinitionId, slotId, BindingKind.InheritedLiveCarrier);

    public void ChooseNeutral(string editDefinitionId, string slotId) =>
        Choose(editDefinitionId, slotId, BindingKind.Neutral);

    public void ChooseHidden(string editDefinitionId, string slotId) =>
        Choose(editDefinitionId, slotId, BindingKind.Hidden);

    public void ChooseSourceSlot(string editDefinitionId, string slotId, string sourceSlotId,
        string? sourceEditDefinitionId = null) => Change(project =>
            SetBinding(project, editDefinitionId, new Binding
            {
                SlotId = slotId,
                Kind = BindingKind.SourceSlot,
                SourceSlot = new BindingSourceSlot
                {
                    SlotId = sourceSlotId,
                    EditDefinitionId = sourceEditDefinitionId,
                },
            }));

    /// <summary>Normalize returned bytes into a new immutable project asset and atomically bind only the
    /// addressed slot. Existing canonical assets are never overwritten: a changed shared binding therefore
    /// splits on its first mutation, while an unchanged return commits no authored transaction.</summary>
    /// <param name="bakedRest">geometry only: the scene-rest uprighting the session file the return
    /// came back through was baked by, so the asset states its own space (see
    /// <see cref="ProjectAsset.BakedRest"/>).</param>
    public ExactAssetPublishResult PublishAssetForBinding(ProjectAssetIngressSession ingress,
        ProjectAssetKind kind, string label, ProjectAssetNormalization normalization,
        ProjectAssetSource? source = null, int? replacementSubmeshCount = null,
        IReadOnlyList<float>? bakedRest = null)
    {
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(normalization);
        // Normalization is the expensive half and it holds no lock: only the record below is the
        // transaction, which is what lets a caller publish from a worker without stalling the window.
        var staged = StagePublish(Snapshot(), ingress, kind, label, normalization, source,
            replacementSubmeshCount, bakedRest);
        try
        {
            if (staged is null) return new ExactAssetPublishResult(ProjectAssetPublishResult.Unchanged, null, null);
            var result = new ExactAssetPublishResult(ProjectAssetPublishResult.Unchanged, null, null);
            ChangeWithFiles((project, files) =>
            {
                files.Staging(staged.Staged);
                result = RecordPublish(project, files, staged);
            });
            return result;
        }
        finally { DeleteQuietly(staged?.Staged); }
    }

    /// <summary>One publish's whole FILE half, done against a project it does not touch: the addressed
    /// slot's answers are checked, the canonical destination is named, and the returned bytes are
    /// normalized into staging beside it. Null is the unchanged answer — the returned bytes mean the same
    /// thing the project already holds, so there is no transaction to open.</summary>
    private static StagedPublish? StagePublish(AuthoredProject project,
        ProjectAssetIngressSession ingress, ProjectAssetKind kind, string label,
        ProjectAssetNormalization normalization, ProjectAssetSource? source, int? replacementSubmeshCount,
        IReadOnlyList<float>? bakedRest)
    {
        if (project.RootDir is null) throw new InvalidOperationException("project has no root directory");
        var edit = RequiredEdit(project, ingress.EditDefinitionId);
        // The binding itself is read again at the record, where it decides something; here it only has to
        // exist, so a transport addressing a slot this edit does not answer for fails before any bytes move.
        if (!edit.Bindings.Any(candidate =>
                string.Equals(candidate.SlotId, ingress.SlotId, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"edit '{edit.Id}' has no binding for slot '{ingress.SlotId}'");
        var slot = project.TargetSlots.Single(candidate =>
            string.Equals(candidate.Id, ingress.SlotId, StringComparison.Ordinal));
        if (replacementSubmeshCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(replacementSubmeshCount),
                "a replacement cannot have a negative number of submeshes");
        if (replacementSubmeshCount is not null && slot.Input != TargetInputKind.Geometry)
            throw new InvalidOperationException("replacement output layout can only accompany geometry");
        if (bakedRest is not null && slot.Input != TargetInputKind.Geometry)
            throw new InvalidOperationException("a baked rest can only accompany geometry");
        ProjectAssetIngress.RequireUnchangedBinding(project, ingress);
        ProjectAssetIngress.RequireUnchangedSource(project, ingress);

        string returned = File.Exists(ingress.ReturnArtifact)
            ? ingress.ReturnArtifact : ingress.OutboundSnapshot;
        if (!File.Exists(returned))
            throw new FileNotFoundException("the transport return artifact is missing", returned);
        // The seam under the arm that consumes what it reads: a lent transport's return artifact is the
        // only copy of what an outside program sent, and a retry after a failure here reads it again.
        if (normalization.ConsumesSource && !ingress.HandedOver)
            throw new InvalidOperationException("a normalization that consumes its source needs a "
                + "transport whose bytes were handed over");
        string assetId = Id("asset");
        string extension = kind switch
        {
            ProjectAssetKind.Geometry => ".glb",
            ProjectAssetKind.Picture => ".png",
            ProjectAssetKind.Ramp => ".dds",
            _ => Path.GetExtension(returned),
        };
        string relative = slot.Input == TargetInputKind.Geometry
            ? $"assets/edits/{StorageSegment(edit.Id)}/geometry/{assetId}{extension}"
            : $"assets/edits/{StorageSegment(edit.Id)}/slots/{StorageSegment(slot.Id)}/{assetId}{extension}";
        string canonical = ProjectAssetIngress.Resolve(project, relative);
        string directory = Path.GetDirectoryName(canonical)!;
        var minted = ProjectAssetIngress.MintDirectory(directory);
        string staged = Path.Combine(directory, $".{assetId}.{Guid.NewGuid():N}.stage");
        try
        {
            normalization.Normalize(returned, staged);
            if (ingress.HasSemanticBaseline
                && string.Equals(normalization.SemanticIdentity(ingress.SourceArtifact),
                    normalization.StagedIdentity ?? normalization.SemanticIdentity(staged),
                    StringComparison.Ordinal))
            {
                DeleteQuietly(staged);
                return null;
            }
        }
        catch { DeleteQuietly(staged); throw; }
        var lineage = source is not null ? Clone(source)
            : ingress.SourceProjectAssetId is { } sourceId
                ? new ProjectAssetSource { ProjectAssetId = sourceId } : null;
        return new StagedPublish(ingress, kind,
            string.IsNullOrWhiteSpace(label) ? Path.GetFileName(relative) : label.Trim(),
            assetId, relative, canonical, staged, lineage, replacementSubmeshCount, minted, bakedRest);
    }

    /// <summary>One publish's MUTATION half: the staged bytes take their canonical place and the addressed
    /// binding names the new asset. Both belong to whichever transaction runs this — the file move is
    /// recorded for rollback, and the transport's own baseline is only advanced once the intent is live,
    /// so a compound change that refuses anywhere leaves neither behind.</summary>
    private static ExactAssetPublishResult RecordPublish(AuthoredProject project, TransactionFiles files,
        StagedPublish staged)
    {
        var ingress = staged.Ingress;
        var edit = RequiredEdit(project, ingress.EditDefinitionId);
        var binding = edit.Bindings.SingleOrDefault(candidate =>
                string.Equals(candidate.SlotId, ingress.SlotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"edit '{edit.Id}' has no binding for slot '{ingress.SlotId}'");
        if (!string.Equals(ProjectAssetIngress.BindingIdentity(binding),
                ingress.StartingBindingIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the exact slot changed while its returned bytes were being normalized");
        File.Move(staged.Staged, staged.Canonical, overwrite: false);
        files.Created(staged.Canonical);
        files.MintedDirectories(staged.MintedDirectories);
        files.MintedDirectories(ingress.MintedDirectories);
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = staged.AssetId,
            Kind = staged.Kind,
            Label = staged.Label,
            File = staged.Relative,
            Source = staged.Lineage,
            BakedRest = staged.BakedRest?.ToList(),
        });
        SetBinding(project, edit.Id, new Binding
        {
            SlotId = ingress.SlotId,
            Kind = BindingKind.ProjectAsset,
            ProjectAssetId = staged.AssetId,
        });
        if (staged.ReplacementSubmeshCount is { } count)
            RecordReplacementOutputs(project, edit.Id, count);
        // A handed-over transport is done the moment this returns: it has no snapshots to refresh and
        // nothing will ever address it again, so it is owed nothing here.
        if (!ingress.HandedOver)
        {
            // Three separate owings, so one that will not go does not take the other two with it: the
            // editor that is holding the outbound snapshot open is the measured case, and it must not cost
            // this transport the advanced baseline that lets the same editor save again.
            files.AfterCommit(() => File.Copy(staged.Canonical, ingress.OutboundSnapshot, overwrite: true));
            files.AfterCommit(() => File.Copy(staged.Canonical, ingress.SourceArtifact, overwrite: true));
            files.AfterCommit(() => ingress.Advance(staged.AssetId, ingress.SourceArtifact,
                ProjectAssetIngress.FileIdentity(staged.Canonical)));
        }
        return new ExactAssetPublishResult(ProjectAssetPublishResult.Published, staged.AssetId,
            staged.Relative);
    }

    /// <summary>A publish whose bytes are normalized and waiting beside their canonical place, carrying
    /// everything the transaction still owes it.</summary>
    private sealed record StagedPublish(ProjectAssetIngressSession Ingress, ProjectAssetKind Kind,
        string Label, string AssetId, string Relative, string Canonical, string Staged,
        ProjectAssetSource? Lineage, int? ReplacementSubmeshCount,
        IReadOnlyList<string> MintedDirectories, IReadOnlyList<float>? BakedRest);

    /// <summary>Apply only the accepted binding rows from a reviewable material-source proposal. Dynamic
    /// and unsupported differences remain visible on the proposal and can never become guessed bindings.</summary>
    public void AcceptMaterialSource(MaterialSourceProposal proposal, IEnumerable<string> acceptedSlotIds)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var accepted = acceptedSlotIds.ToHashSet(StringComparer.Ordinal);
        var proposed = proposal.Differences.Select(difference => difference.SlotId)
            .ToHashSet(StringComparer.Ordinal);
        if (accepted.Except(proposed).FirstOrDefault() is { } unknown)
            throw new KeyNotFoundException($"material proposal has no difference for slot '{unknown}'");
        Change(project =>
        {
            RequiredEdit(project, proposal.EditDefinitionId);
            foreach (var difference in proposal.Differences.Where(d => accepted.Contains(d.SlotId)))
            {
                if (difference.Disposition != MaterialDifferenceDisposition.Binding
                    || difference.ProposedBinding is null)
                    throw new InvalidOperationException($"material difference '{difference.SlotId}' is not bindable");
                if (!string.Equals(difference.ProposedBinding.SlotId, difference.SlotId,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"material difference '{difference.SlotId}' proposes another slot");
                SetBinding(project, proposal.EditDefinitionId, Clone(difference.ProposedBinding));
            }
        });
    }

    private void Choose(string editDefinitionId, string slotId, BindingKind kind) => Change(project =>
        SetBinding(project, editDefinitionId, new Binding { SlotId = slotId, Kind = kind }));

    /// <summary>Run one transaction's mutation against its candidate. The flag is what makes a command
    /// opened INSIDE another one loud rather than silent: the lock is re-entrant, so a nested command would
    /// commit against the LIVE project while the outer candidate is still in flight, and the outer's own
    /// commit would then throw that work away. A caller inside a compound has the compound's own face to
    /// make the command on.</summary>
    private void Mutate(Action work)
    {
        if (_inTransaction)
            throw new InvalidOperationException("an authored transaction is already open; a command made "
                + "while one is running belongs on the compound change it was given");
        _inTransaction = true;
        try { work(); }
        finally { _inTransaction = false; }
    }

    private void Change(Action<AuthoredProject> change)
    {
        AuthoredProjectChangedEventArgs? committed = null;
        lock (_gate)
        {
            var previous = _project;
            var candidate = Clone(previous);
            Mutate(() => change(candidate));
            var errors = AuthoredProjectValidator.Errors(candidate);
            if (errors.Count > 0)
                throw new InvalidDataException("authored edit was refused: " + string.Join("; ", errors));
            if (SameJson(previous, candidate)
                && string.Equals(previous.RootDir, candidate.RootDir, StringComparison.OrdinalIgnoreCase))
                return;
            _project = candidate;
            committed = Describe(previous, candidate, ++_revision);
        }
        if (committed is not null) Changed?.Invoke(this, committed);
    }

    /// <summary>Commit authored intent and the project files that intent names as ONE transaction. The
    /// mutation runs against a candidate, and every file it puts in place is recorded here: a candidate
    /// that refuses, or a mutation that throws part-way, takes those files back out again, so the folder
    /// is left as the transaction found it. What is only owed once the intent is LIVE — a transport's
    /// advanced baseline — waits for <see cref="TransactionFiles.Complete"/>, after the change is
    /// announced, which is the order a single publish has always had.</summary>
    private void ChangeWithFiles(Action<AuthoredProject, TransactionFiles> change)
    {
        AuthoredProjectChangedEventArgs? committed = null;
        var files = new TransactionFiles();
        try
        {
            lock (_gate)
            {
                var previous = _project;
                var candidate = Clone(previous);
                try
                {
                    Mutate(() => change(candidate, files));
                    var errors = AuthoredProjectValidator.Errors(candidate);
                    if (errors.Count > 0)
                        throw new InvalidDataException("authored edit was refused: "
                            + string.Join("; ", errors));
                }
                catch { files.RollBack(); throw; }
                if (SameJson(previous, candidate)
                    && string.Equals(previous.RootDir, candidate.RootDir, StringComparison.OrdinalIgnoreCase))
                {
                    files.RollBack();
                    return;
                }
                _project = candidate;
                committed = Describe(previous, candidate, ++_revision);
            }
            if (committed is not null) Changed?.Invoke(this, committed);
            files.Complete();
        }
        finally { files.CleanStaging(); }
    }

    /// <summary>The files one transaction is putting in place, and what it owes them either way.</summary>
    internal sealed class TransactionFiles
    {
        private readonly List<string> _created = new();
        private readonly List<string> _directories = new();
        private readonly List<string> _minted = new();
        private readonly List<string> _staging = new();
        private readonly List<Action> _afterCommit = new();

        /// <summary>A file this transaction moved into its canonical place. Nothing was ever there — the
        /// move refuses to overwrite — so undoing it is deleting it.</summary>
        public void Created(string file) => _created.Add(file);

        /// <summary>A transport folder this transaction minted, with the snapshots inside it.</summary>
        public void CreatedDirectory(string directory) => _directories.Add(directory);

        /// <summary>A folder level this transaction minted on the way to somewhere — an edit's assets
        /// folder, a transport's edit and slot levels. Taken back out on a rollback only when it is EMPTY:
        /// what it holds, if anything, belongs to work this transaction is not undoing.</summary>
        public void MintedDirectory(string directory) => _minted.Add(directory);

        /// <inheritdoc cref="MintedDirectory(string)"/>
        public void MintedDirectories(IEnumerable<string> directories) => _minted.AddRange(directories);

        /// <summary>Working bytes to drop whichever way the transaction goes.</summary>
        public void Staging(string file) => _staging.Add(file);

        public void AfterCommit(Action work) => _afterCommit.Add(work);

        public void RollBack()
        {
            CleanStaging();
            for (int i = _created.Count - 1; i >= 0; i--) DeleteQuietly(_created[i]);
            for (int i = _directories.Count - 1; i >= 0; i--)
                try { Directory.Delete(_directories[i], recursive: true); } catch { /* best-effort */ }
            // Deepest first, so a level emptied by the one below it goes with it.
            foreach (string directory in _minted.OrderByDescending(level =>
                         level.Count(character => character == Path.DirectorySeparatorChar)))
                try
                {
                    if (Directory.Exists(directory)
                        && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch { /* best-effort */ }
            _created.Clear();
            _directories.Clear();
            _minted.Clear();
            _afterCommit.Clear();
        }

        public void CleanStaging()
        {
            foreach (string file in _staging) DeleteQuietly(file);
            _staging.Clear();
        }

        /// <summary>What is owed once the intent is LIVE — a transport's advanced baseline, the snapshots
        /// beside it. Each is run on its own and its failure is swallowed, which is what makes the
        /// all-or-nothing promise TRUE: by the time this runs the transaction has committed, the change is
        /// announced and the slot is rebound, so a throw out of here would leave a caller reporting
        /// "nothing was changed" over a change that fully happened.
        ///
        /// <para>What a failure here costs is exactly its own nicety. Nothing it writes is a file another
        /// route needs to EXIST — every one of them is an overwrite of a snapshot the transport already
        /// has, so a reopen still finds what it needs — and the baseline it advances, left unadvanced,
        /// makes the next save from the same open editor refuse with the sentence for exactly that state
        /// (<see cref="ProjectAssetIngress.EditMovedWhileOpen"/>), which tells the modder to open it
        /// again.</para></summary>
        public void Complete()
        {
            foreach (var work in _afterCommit)
                try { work(); } catch { /* best-effort: the intent is already live */ }
            _afterCommit.Clear();
        }
    }

    private static void DeleteQuietly(string? file)
    {
        if (file is null) return;
        try { if (File.Exists(file)) File.Delete(file); } catch { /* best-effort */ }
    }

    private sealed record ValueFileWrite(string File, string Semantic, string Value);

    /// <summary>Commit authored intent and its tiny value files as one transaction. The candidate is fully
    /// validated before any file is touched; file writes keep byte backups until every write succeeds, and
    /// only then does the candidate become the live project.</summary>
    private void ChangeWithValueFiles(Action<AuthoredProject, List<ValueFileWrite>> change)
    {
        AuthoredProjectChangedEventArgs? committed = null;
        lock (_gate)
        {
            var previous = _project;
            var candidate = Clone(previous);
            var writes = new List<ValueFileWrite>();
            Mutate(() => change(candidate, writes));
            var errors = AuthoredProjectValidator.Errors(candidate);
            if (errors.Count > 0)
                throw new InvalidDataException("authored edit was refused: " + string.Join("; ", errors));
            if (SameJson(previous, candidate)
                && string.Equals(previous.RootDir, candidate.RootDir, StringComparison.OrdinalIgnoreCase))
                return;
            committed = Describe(previous, candidate, _revision + 1);
            WriteValueFiles(candidate, writes);
            _project = candidate;
            _revision++;
        }
        if (committed is not null) Changed?.Invoke(this, committed);
    }

    private static void SetMaterialValue(AuthoredProject project, string editDefinitionId,
        string slotId, MaterialValueField field, string semantic, string canonical,
        List<ValueFileWrite> writes)
    {
        var edit = RequiredEdit(project, editDefinitionId);
        var binding = edit.Bindings.SingleOrDefault(candidate =>
                string.Equals(candidate.SlotId, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"edit '{editDefinitionId}' has no binding for slot '{slotId}'");
        var slot = project.TargetSlots.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"no target slot '{slotId}'");
        if (slot.Input != TargetInputKind.MaterialValue
            || !string.Equals(slot.Semantic, semantic, StringComparison.Ordinal))
            throw new InvalidOperationException($"slot '{slotId}' does not carry shading value '{semantic}'");

        ProjectAsset? asset = binding.Kind == BindingKind.ProjectAsset
            && binding.ProjectAssetId is { } currentId
            ? project.ProjectAssets.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, currentId, StringComparison.Ordinal)) : null;
        if (asset is { Kind: ProjectAssetKind.StructuredValue, Value: { } current }
            && string.Equals(current.Semantic, semantic, StringComparison.Ordinal))
        {
            if (string.Equals(current.Value, canonical, StringComparison.Ordinal)) return;
            current.Value = canonical;
        }
        else
        {
            string file = StableValueFile(editDefinitionId, slotId, semantic);
            asset = project.ProjectAssets.FirstOrDefault(candidate =>
                candidate.Kind == ProjectAssetKind.StructuredValue
                && string.Equals(candidate.File, file, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Value?.Semantic, semantic, StringComparison.Ordinal)
                && !project.EditDefinitions.SelectMany(candidateEdit => candidateEdit.Bindings)
                    .Any(candidateBinding => string.Equals(candidateBinding.ProjectAssetId,
                        candidate.Id, StringComparison.Ordinal)));
            if (asset is null)
            {
                asset = new ProjectAsset
                {
                    Id = Id("asset"),
                    Kind = ProjectAssetKind.StructuredValue,
                    Label = field.Label,
                    File = file,
                    Value = new ProjectAssetValue { Semantic = semantic, Value = canonical },
                };
                project.ProjectAssets.Add(asset);
            }
            else
            {
                asset.Label = field.Label;
                asset.Value!.Value = canonical;
            }
            SetBinding(project, editDefinitionId, new Binding
            {
                SlotId = slotId,
                Kind = BindingKind.ProjectAsset,
                ProjectAssetId = asset.Id,
            });
        }
        writes.RemoveAll(write => string.Equals(write.File, asset.File,
            StringComparison.OrdinalIgnoreCase));
        writes.Add(new ValueFileWrite(asset.File, semantic, canonical));
    }

    private static string StableValueFile(string editDefinitionId, string slotId, string semantic)
    {
        static string Stamp(string value)
        {
            var text = new StringBuilder();
            foreach (char c in value.ToLowerInvariant())
                text.Append(char.IsLetterOrDigit(c) ? c : '-');
            return text.ToString().Trim('-');
        }
        string digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(slotId + "\0" + semantic))).ToLowerInvariant()[..12];
        string editDigest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(editDefinitionId))).ToLowerInvariant()[..8];
        return $"values/answer-{Stamp(editDefinitionId)}-{editDigest}/"
            + $"shading-{Stamp(slotId)}-{Stamp(semantic)}-{digest}.json";
    }

    private static ArgumentException MaterialValueArgument(MaterialValueField field, string value) =>
        new(field.Kind == MaterialValueKind.Color
            ? $"'{value}' is not four numbers"
            : field.Source == MaterialValueSource.FamilyRule
                ? $"'{value}' is not 0 or 1"
                : $"'{value}' is not a number", nameof(value));

    private static void WriteValueFiles(AuthoredProject project, IReadOnlyList<ValueFileWrite> writes)
    {
        if (writes.Count == 0) return;
        string root = project.RootDir
            ?? throw new InvalidOperationException("project has no root directory");
        var changed = new List<(string File, bool Existed, byte[]? Bytes)>();
        var staged = new List<string>();
        try
        {
            foreach (var write in writes)
            {
                string full = Path.Combine(root,
                    write.File.Replace('/', Path.DirectorySeparatorChar));
                string directory = Path.GetDirectoryName(full)!;
                Directory.CreateDirectory(directory);
                string stage = Path.Combine(directory, "." + Path.GetFileName(full)
                    + "." + Guid.NewGuid().ToString("N") + ".stage");
                staged.Add(stage);
                string json = JsonSerializer.Serialize(new
                {
                    semantic = write.Semantic,
                    value = write.Value,
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(stage, json, new UTF8Encoding(false));
                bool existed = File.Exists(full);
                changed.Add((full, existed, existed ? File.ReadAllBytes(full) : null));
                File.Move(stage, full, overwrite: true);
                staged.Remove(stage);
            }
        }
        catch
        {
            foreach (var prior in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (prior.Existed) File.WriteAllBytes(prior.File, prior.Bytes!);
                    else if (File.Exists(prior.File)) File.Delete(prior.File);
                }
                catch { }
            }
            throw;
        }
        finally
        {
            foreach (string stage in staged)
                try { if (File.Exists(stage)) File.Delete(stage); } catch { }
        }
    }

    private static void RemoveUnauthoredMaterialValueSlots(AuthoredProject project)
    {
        foreach (var slot in project.TargetSlots.Where(slot =>
                     slot.Input == TargetInputKind.MaterialValue).ToList())
        {
            var bindings = project.EditDefinitions.SelectMany(edit => edit.Bindings)
                .Where(binding => string.Equals(binding.SlotId, slot.Id, StringComparison.Ordinal)).ToList();
            bool borrowed = project.EditDefinitions.SelectMany(edit => edit.Bindings)
                .Any(binding => binding.SourceSlot is { } source
                    && string.Equals(source.SlotId, slot.Id, StringComparison.Ordinal));
            if (borrowed || bindings.Any(binding => binding.Kind != BindingKind.TargetGameValue)) continue;
            foreach (var edit in project.EditDefinitions)
                edit.Bindings.RemoveAll(binding => string.Equals(binding.SlotId, slot.Id,
                    StringComparison.Ordinal));
            project.TargetSlots.Remove(slot);
        }
    }

    /// <summary>Save-time normalization: remove value slots with no authored answer, then structured-value
    /// assets no binding or lineage references. A changed sweep advances the revision but raises no event:
    /// a save must not re-enter page refresh.</summary>
    internal IReadOnlyList<string> SweepStructuredValuesForSave()
    {
        lock (_gate)
        {
            var candidate = Clone(_project);
            var removed = new List<ProjectAsset>();
            // A committer like any other, and it carries the same flag: a save that ran INSIDE an open
            // transaction would sweep the live project out from under the candidate still in flight.
            Mutate(() =>
            {
                RemoveUnauthoredMaterialValueSlots(candidate);
                var referenced = candidate.EditDefinitions.SelectMany(edit => edit.Bindings)
                    .Select(binding => binding.ProjectAssetId)
                    .Concat(candidate.ProjectAssets.Select(asset => asset.Source?.ProjectAssetId))
                    .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
                removed.AddRange(candidate.ProjectAssets.Where(asset =>
                    asset.Kind == ProjectAssetKind.StructuredValue && !referenced.Contains(asset.Id)));
                candidate.ProjectAssets.RemoveAll(asset => removed.Contains(asset));
            });
            var errors = AuthoredProjectValidator.Errors(candidate);
            if (errors.Count > 0)
                throw new InvalidDataException("authored save sweep was refused: "
                    + string.Join("; ", errors));
            bool changed = !SameJson(_project, candidate)
                || !string.Equals(_project.RootDir, candidate.RootDir,
                    StringComparison.OrdinalIgnoreCase);
            _project = candidate;
            if (changed) _revision++;
            return removed.Select(asset => asset.File).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private static AuthoredProjectChangedEventArgs Describe(AuthoredProject previous,
        AuthoredProject current, long revision)
    {
        var editIds = ChangedIds(previous.EditDefinitions, current.EditDefinitions, edit => edit.Id);
        var slotIds = ChangedIds(previous.TargetSlots, current.TargetSlots, slot => slot.Id);
        var beforeEdits = previous.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        var afterEdits = current.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        foreach (string editId in editIds)
        {
            if (beforeEdits.TryGetValue(editId, out var before))
                slotIds.UnionWith(before.Bindings.Select(binding => binding.SlotId));
            if (afterEdits.TryGetValue(editId, out var after))
                slotIds.UnionWith(after.Bindings.Select(binding => binding.SlotId));
        }
        ExpandBorrowers(previous, current, slotIds, editIds);

        AuthoredInvalidation invalidation = AuthoredInvalidation.None;
        if (!SameJson(previous.Info, current.Info))
            invalidation |= AuthoredInvalidation.Identity;
        if (!SameJson(previous.AuthoredAgainst, current.AuthoredAgainst)
            || !string.Equals(previous.RootDir, current.RootDir, StringComparison.OrdinalIgnoreCase))
            invalidation |= AuthoredInvalidation.Metadata;
        if (!SameJson(previous.WorkspaceIndex, current.WorkspaceIndex))
            invalidation |= AuthoredInvalidation.Workspace | AuthoredInvalidation.Preview;
        if (!SameJson(previous.ProjectAssets, current.ProjectAssets))
            invalidation |= AuthoredInvalidation.Assets | AuthoredInvalidation.Preview;
        if (!SameJson(previous.TargetSlots, current.TargetSlots))
            invalidation |= AuthoredInvalidation.Slots | AuthoredInvalidation.Preview;
        if (!SameJson(previous.EditDefinitions, current.EditDefinitions))
            invalidation |= AuthoredInvalidation.Bindings | AuthoredInvalidation.Preview;
        // Placement carries no picture. What a render is made of is one edit's own bindings and the files
        // they name, so moving an edit onto the board, or between key-group states, changes what SHIPS and
        // never what any row draws.
        if (!SameJson(previous.Always, current.Always)
            || !SameJson(previous.KeyGroups, current.KeyGroups))
            invalidation |= AuthoredInvalidation.Composition;

        return new AuthoredProjectChangedEventArgs(revision,
            editIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            slotIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(), invalidation);
    }

    /// <summary>Grow a change's named slots by the slots that BORROW them, and name the edits doing the
    /// borrowing.
    ///
    /// <para>A binding can take its value from another slot, and everything derived from that borrowing slot
    /// — the card's picture, and the render that samples the file it resolves to — is made of the source's
    /// answer rather than of anything the borrower itself holds. A change naming only the source would
    /// therefore leave the borrower's pictures standing while they are pictures of the source's OLD answer.
    /// The dependency runs one way and is written only on the borrower, so it takes this reverse pass to
    /// see.</para>
    ///
    /// <para>Run to a fixed point: a borrowed slot can itself be borrowed, and one pass down the list
    /// carries an answer only as far as the list's own order allows. Only the borrowing SLOT is added — the
    /// borrowing edit's other slots did not move — while the edit itself is named because its render samples
    /// what its slots resolve to.</para>
    ///
    /// <para>Borrowings are collected from BOTH projects, which is insurance rather than what names a
    /// borrower the change removed: an edit the change dropped, or whose bindings it rewrote, is already
    /// named above by the changed-edit pass, and that pass unions in the slots it used to bind. Reading the
    /// before project as well costs one more list and closes the question.</para>
    ///
    /// <para>A borrowing is matched by the source's SLOT id alone: a source names an edit as well, and two
    /// edits can answer the same slot differently, so a change to one of them names the borrowers of both.
    /// The cost of naming one too many is a re-render of a picture that did not move, and the one consumer
    /// is the page's invalidation, so the safe direction is the one taken.</para></summary>
    private static void ExpandBorrowers(AuthoredProject previous, AuthoredProject current,
        HashSet<string> slotIds, HashSet<string> editIds)
    {
        var borrowings = previous.EditDefinitions.Concat(current.EditDefinitions)
            .SelectMany(edit => edit.Bindings
                .Where(binding => binding.SourceSlot is { SlotId.Length: > 0 })
                .Select(binding => (Edit: edit.Id, binding.SlotId, Source: binding.SourceSlot!.SlotId)))
            .ToList();
        if (borrowings.Count == 0 || slotIds.Count == 0) return;
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var borrowing in borrowings)
            {
                if (!slotIds.Contains(borrowing.Source)) continue;
                grew |= slotIds.Add(borrowing.SlotId);
                editIds.Add(borrowing.Edit);
            }
        }
    }

    private static HashSet<string> ChangedIds<T>(IEnumerable<T> before, IEnumerable<T> after,
        Func<T, string> id)
    {
        var left = before.ToDictionary(id, Signature, StringComparer.Ordinal);
        var right = after.ToDictionary(id, Signature, StringComparer.Ordinal);
        return left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal)
            .Where(key => !left.TryGetValue(key, out string? l)
                || !right.TryGetValue(key, out string? r)
                || !string.Equals(l, r, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool SameJson<T>(T left, T right) =>
        string.Equals(Signature(left), Signature(right), StringComparison.Ordinal);
    private static string Signature<T>(T value) => JsonSerializer.Serialize(value);

    private static void SetBinding(AuthoredProject project, string editDefinitionId, Binding binding)
    {
        var edit = RequiredEdit(project, editDefinitionId);
        int index = edit.Bindings.FindIndex(b => string.Equals(b.SlotId, binding.SlotId, StringComparison.Ordinal));
        if (index < 0) throw new KeyNotFoundException(
            $"edit '{editDefinitionId}' has no binding for slot '{binding.SlotId}'");
        var slot = project.TargetSlots.Single(candidate =>
            string.Equals(candidate.Id, binding.SlotId, StringComparison.Ordinal));
        if (slot.Input == TargetInputKind.Geometry && !SameJson(edit.Bindings[index], binding))
            edit.ReturnWarning = null;
        edit.Bindings[index] = binding;
    }

    private static string? NormalizeReturnWarning(string? warning) =>
        string.IsNullOrWhiteSpace(warning) ? null : warning.Trim();

    /// <summary>Every slot a content edit for one part answers for, in a stable order: the part's game-domain
    /// slots less its visibility. Domain and structural route are the whole test.</summary>
    private static List<TargetSlot> ContentSlots(AuthoredProject project, TargetPart target) =>
        project.TargetSlots
            .Where(slot => slot.Part.SameAs(target) && slot.Domain == TargetSlotDomain.Game
                && slot.Input != TargetInputKind.Visibility)
            .OrderBy(slot => slot.Id, StringComparer.Ordinal).ToList();

    /// <summary>One slot per place a content edit for the part answers for. A part may carry several slot
    /// records for the same route, and they all address the same game object, so a new edit picks one of
    /// each rather than the whole pile. The lowest slot id makes the same project and command choose
    /// deterministically.</summary>
    private static List<TargetSlot> ContentSlotRoutes(AuthoredProject project, TargetPart target)
    {
        var routes = new List<TargetSlot>();
        foreach (var slot in ContentSlots(project, target))
        {
            int index = routes.FindIndex(chosen => chosen.SameRoute(slot));
            if (index < 0) routes.Add(slot);
        }
        return routes;
    }

    /// <summary>A fresh id for a copy of each named slot, minted over committed state so the same command on
    /// the same project mints the same ids. The copies themselves are added by <see cref="AddSlotCopies"/>
    /// inside the transaction.</summary>
    private static Dictionary<string, string> MintSlotCopyIds(AuthoredProject project,
        IEnumerable<string> sourceSlotIds)
    {
        var taken = project.TargetSlots.Select(slot => slot.Id).ToList();
        var minted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string source in sourceSlotIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            string id = MintId("slot", taken);
            minted.Add(source, id);
            taken.Add(id);
        }
        return minted;
    }

    /// <summary>Make one exact copy of each slot in <paramref name="slotIds"/>, identical to the original but
    /// for its id. What the copies address is untouched: the
    /// copy of a game slot is a second slot on the same game object, which is how one part's second edit and
    /// a duplicate both come to hold slots of their own.</summary>
    private static void AddSlotCopies(AuthoredProject project,
        IReadOnlyDictionary<string, string> slotIds)
    {
        foreach (var slot in project.TargetSlots.Where(slot => slotIds.ContainsKey(slot.Id)).ToList())
        {
            var copy = Clone(slot);
            copy.Id = slotIds[slot.Id];
            copy.OwnerEditId = null;
            project.TargetSlots.Add(copy);
        }
    }

    /// <summary>Whether the part already has an answer on the board — any edit of it used in Always or in a
    /// key-group state. This is the whole creation-time activation rule, and both minting commands read it:
    /// a hide is an ordinary edit, so the first edit a part gets is active wherever it comes from, and every
    /// later one waits in the library for a placement.</summary>
    private static bool HasPlacement(AuthoredProject project, TargetPart part)
    {
        var owned = project.EditDefinitions.Where(edit => edit.Target.SameAs(part))
            .Select(edit => edit.Id).ToHashSet(StringComparer.Ordinal);
        return project.Always.Any(owned.Contains)
            || project.KeyGroups.SelectMany(group => group.States)
                .SelectMany(state => state.ActiveEditIds).Any(owned.Contains);
    }

    /// <summary>Whether the project has heard of a part through a target slot or an authored edit. This is
    /// the set the validator will accept an additional hide against.</summary>
    private static bool IsKnownPart(AuthoredProject project, TargetPart part) =>
        project.TargetSlots.Any(slot => slot.Part.SameAs(part))
        || project.EditDefinitions.Any(edit => edit.Target.SameAs(part));

    /// <summary>The name a new content edit is given when the caller supplies none: the next one up from
    /// however many the part already has, moved past a name the user has already typed onto one of them.</summary>
    public static string NewEditLabel(AuthoredProject project, TargetPart target, string? label)
    {
        var siblings = project.EditDefinitions
            .Where(edit => edit.Kind == EditDefinitionKind.Content && edit.Target.SameAs(target)).ToList();
        var taken = siblings.Select(edit => edit.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string requested = label?.Trim() ?? "";
        if (requested.Length > 0 && !taken.Contains(requested)) return requested;
        for (int n = siblings.Count + 1; ; n++)
            if (taken.Add($"Edit {n}")) return $"Edit {n}";
    }

    /// <summary>The name an existing edit falls back to when its own is cleared: where it sits among the
    /// part's content edits, moved past a name one of its siblings has already been given, or the one name a
    /// hide has. Without that walk a name the user typed onto one edit could be handed to a second by
    /// clearing its own, which is the one way two of a part's edits could end up called the same thing.</summary>
    private static string DefaultEditLabel(AuthoredProject project, EditDefinition edit)
    {
        if (edit.Kind == EditDefinitionKind.Hide) return "Hidden";
        var siblings = project.EditDefinitions
            .Where(other => other.Kind == EditDefinitionKind.Content && other.Target.SameAs(edit.Target))
            .ToList();
        int position = siblings.FindIndex(other => string.Equals(other.Id, edit.Id, StringComparison.Ordinal));
        var taken = siblings.Where(other => !string.Equals(other.Id, edit.Id, StringComparison.Ordinal))
            .Select(other => other.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = position + 1; ; n++)
            if (!taken.Contains($"Edit {n}")) return $"Edit {n}";
    }

    /// <summary>The first <c>prefix-NNNN</c> id nothing in <paramref name="taken"/> holds, in the format the
    /// compatibility surface mints. Counting from one over committed state keeps the same command producing
    /// the same id.</summary>
    private static string MintId(string prefix, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        for (int n = 1; ; n++)
        {
            string id = $"{prefix}-{n:D4}";
            if (used.Add(id)) return id;
        }
    }

    private static EditDefinition RequiredEdit(AuthoredProject project, string id) =>
        project.EditDefinitions.SingleOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"edit definition '{id}' does not exist");

    private static string NormalizeFile(string file) => file.Replace('\\', '/');
    private static string StorageSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException($"'{value}' is not a safe asset-storage id");
        return value;
    }
    private static string Id(string kind) => $"{kind}-{Guid.NewGuid():N}";

    private static AuthoredProject Clone(AuthoredProject source)
    {
        string? root = source.RootDir;
        var clone = AuthoredProjectSerializer.Deserialize(AuthoredProjectSerializer.Serialize(source));
        clone.RootDir = root;
        // The serializer is deliberately the deep-copy law for authored state. Rejoin the two pieces of
        // live install evidence it intentionally omits so a later transaction can use the measurement that
        // opened this session without leaking it into schema-2 JSON.
        var transient = source.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        foreach (var slot in clone.TargetSlots)
            if (transient.TryGetValue(slot.Id, out var original))
            {
                slot.DrawIndexCount = original.DrawIndexCount;
                slot.MaterialIndexCounts = original.MaterialIndexCounts?.ToArray();
                slot.MaterialBindingPresent = original.MaterialBindingPresent;
            }
        return clone;
    }

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

    private static TargetSlot Clone(TargetSlot source) => new()
    {
        Id = source.Id,
        OwnerEditId = source.OwnerEditId,
        Part = Clone(source.Part),
        Tier = source.Tier,
        SubmeshIndex = source.SubmeshIndex,
        MaterialSlotIndex = source.MaterialSlotIndex,
        Input = source.Input,
        ShaderProperty = source.ShaderProperty,
        Domain = source.Domain,
        Semantic = source.Semantic,
        Renderer = Clone(source.Renderer),
        Mesh = source.Mesh is null ? null : Clone(source.Mesh),
        Material = source.Material is null ? null : Clone(source.Material),
        DrawIndexCount = source.DrawIndexCount,
        MaterialIndexCounts = source.MaterialIndexCounts?.ToArray(),
        MaterialBindingPresent = source.MaterialBindingPresent,
    };

    private static Binding Clone(Binding source) => new()
    {
        SlotId = source.SlotId,
        Kind = source.Kind,
        ProjectAssetId = source.ProjectAssetId,
        SourceSlot = source.SourceSlot is null ? null : new BindingSourceSlot
        {
            SlotId = source.SourceSlot.SlotId,
            EditDefinitionId = source.SourceSlot.EditDefinitionId,
        },
    };

    private static ProjectAsset Clone(ProjectAsset source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        Label = source.Label,
        File = source.File,
        Source = source.Source is null ? null : Clone(source.Source),
        BakedRest = source.BakedRest?.ToList(),
        Value = source.Value is null ? null : new ProjectAssetValue
        {
            Semantic = source.Value.Semantic,
            Value = source.Value.Value,
        },
    };

    private static ProjectAssetSource Clone(ProjectAssetSource source) => new()
    {
        GameAsset = source.GameAsset is null ? null : Clone(source.GameAsset),
        ProjectAssetId = source.ProjectAssetId,
    };
}

/// <summary>What one committed change made stale, so a reader can redo the part of its work the change can
/// have moved instead of all of it. Runtime only: it travels on the change event and is never persisted.
///
/// <para>The grain is the derivation that consumes it. Everything the build plan is derived from says so
/// through a flag of its own; <see cref="Identity"/> is the one thing no part of the plan reads, which is
/// what <see cref="AuthoredInvalidations.AffectsBuildPlan"/> is able to say because of the split.</para></summary>
[Flags]
public enum AuthoredInvalidation
{
    None = 0,

    /// <summary>The <see cref="ProjectInfo"/> changes the build PLAN derives nothing from. That rule is the
    /// flag — not the list of fields, which the record is free to grow — and it is the whole reason
    /// <see cref="AuthoredInvalidations.AffectsBuildPlan"/> can answer no to this one flag alone.
    ///
    /// <para>Today the rule takes all of <see cref="ProjectInfo"/>. Most of it is what the mod calls itself
    /// — name, version, author, description, preview picture, toggle key, and the subject it names itself
    /// after — which only presentation and package naming read. <see cref="ProjectInfo.IncludeRepairData"/>
    /// rides here too, though it is a build-OUTPUT switch rather than identity: the plan does not model
    /// repair-data emission at all, the record is written at build time, and a run replans for itself. If
    /// the plan ever answers for that emission, that switch is what must be split back out under a flag of
    /// its own — left here it would leave a stale verdict on screen with nothing able to say so.</para></summary>
    Identity = 1 << 0,

    /// <summary>Where the project lives on disk, and which catalog it was last authored against. The plan
    /// resolves every authored file under the root to answer whether it is there, so this moves it.</summary>
    Metadata = 1 << 1,

    Workspace = 1 << 2,
    Slots = 1 << 3,
    Bindings = 1 << 4,
    Assets = 1 << 5,

    /// <summary>Which edits are placed, and in which key-group state. It decides what is emitted and
    /// nothing about what any row draws.</summary>
    Composition = 1 << 6,

    /// <summary>Something a rendered picture is made of changed. It never travels alone: the flag naming
    /// WHAT changed rides with it, and the change's edit and slot ids say where.</summary>
    Preview = 1 << 7,
}

public static class AuthoredInvalidations
{
    /// <summary>Whether this change can have moved the build plan. Only <see cref="AuthoredInvalidation.Identity"/>
    /// is answered no; a change no flag classified is answered yes, because an unrecognised difference must
    /// not be able to leave a stale plan on screen.</summary>
    public static bool AffectsBuildPlan(this AuthoredInvalidation invalidation) =>
        invalidation == AuthoredInvalidation.None
        || (invalidation & ~AuthoredInvalidation.Identity) != AuthoredInvalidation.None;

    /// <summary>Whether this change can say WHICH of the project it moved. A change carries the edits and
    /// slots it touched, and everything derived per edit or per slot can be redone for exactly those — but a
    /// workspace recapture re-reads the whole install under them all, and a change that names neither has
    /// nothing to aim with. Both of those are answered no, and a reader with per-item derived state has to
    /// throw all of it away.</summary>
    public static bool NamesWhatItMoved(this AuthoredProjectChangedEventArgs change) =>
        (change.Invalidation & AuthoredInvalidation.Workspace) == AuthoredInvalidation.None
        && (change.EditDefinitionIds.Count > 0 || change.SlotIds.Count > 0);
}

public sealed class AuthoredProjectChangedEventArgs : EventArgs
{
    public AuthoredProjectChangedEventArgs(long revision, IReadOnlyList<string> editDefinitionIds,
        IReadOnlyList<string> slotIds, AuthoredInvalidation invalidation)
    {
        Revision = revision;
        EditDefinitionIds = editDefinitionIds;
        SlotIds = slotIds;
        Invalidation = invalidation;
    }

    public long Revision { get; }
    public IReadOnlyList<string> EditDefinitionIds { get; }
    public IReadOnlyList<string> SlotIds { get; }
    public AuthoredInvalidation Invalidation { get; }
}

public sealed record PartEditState(TargetPart Target, CompositionState State, string? EditDefinitionId);
public sealed record EditSlotState(TargetSlot Slot, Binding Binding, ProjectAsset? ProjectAsset);
public sealed record AuthoredMaterialValueEdit(string Semantic, string? Value);

public enum MaterialDifferenceDisposition
{
    Binding,
    DynamicLive,
    Unsupported,
}

/// <summary>One visible row in a material-source review. Only Binding rows carry a proposed binding.</summary>
public sealed record MaterialSourceDifference(string SlotId, string Label,
    MaterialDifferenceDisposition Disposition, string Detail, Binding? ProposedBinding = null);

public sealed record MaterialSourceProposal(string EditDefinitionId, string SourceLabel,
    IReadOnlyList<MaterialSourceDifference> Differences);

/// <summary>One external-tool round trip addressed by identities, never inferred from its filename. A
/// successful publish advances the canonical baseline so the same open editor can save again.</summary>
public sealed class ProjectAssetIngressSession
{
    internal ProjectAssetIngressSession(string id, string editDefinitionId, string slotId,
        string? sourceProjectAssetId, string sourceArtifact, string outboundSnapshot,
        string returnArtifact, string startingIdentity, bool hasSemanticBaseline,
        string startingBindingIdentity, IReadOnlyList<string>? mintedDirectories = null,
        bool handedOver = false)
    {
        MintedDirectories = mintedDirectories ?? Array.Empty<string>();
        HandedOver = handedOver;
        Id = id;
        EditDefinitionId = editDefinitionId;
        SlotId = slotId;
        SourceProjectAssetId = sourceProjectAssetId;
        SourceArtifact = sourceArtifact;
        OutboundSnapshot = outboundSnapshot;
        ReturnArtifact = returnArtifact;
        StartingIdentity = startingIdentity;
        HasSemanticBaseline = hasSemanticBaseline;
        StartingBindingIdentity = startingBindingIdentity;
    }

    public string Id { get; }
    public string EditDefinitionId { get; }
    public string SlotId { get; }
    public string? SourceProjectAssetId { get; private set; }
    public string SourceArtifact { get; private set; }
    public string OutboundSnapshot { get; }
    public string ReturnArtifact { get; }
    public string StartingIdentity { get; private set; }
    public bool HasSemanticBaseline { get; private set; }
    internal string StartingBindingIdentity { get; private set; }

    /// <summary>The folder levels opening this transport MINTED, deepest first — empty for a transport
    /// reopened onto folders that were already there. A transaction that refuses takes them back out, so a
    /// batch that was never committed leaves no folders named for ids nobody can look up.</summary>
    internal IReadOnlyList<string> MintedDirectories { get; }

    /// <summary>This transport was HANDED bytes for one publish inside one transaction rather than lent to
    /// an outside program — see <see cref="ProjectAssetIngress.Begin"/>. It carries no snapshots of its
    /// own, is owed no advanced baseline, and nothing can reopen it.
    ///
    /// <para>While this is set, <see cref="SourceArtifact"/> is the project's OWN canonical file rather
    /// than a snapshot of it. It is there to be READ as the baseline; writing to it would be writing over
    /// an immutable project asset.</para></summary>
    internal bool HandedOver { get; }

    internal void Advance(string? projectAssetId, string sourceArtifact, string identity)
    {
        SourceProjectAssetId = projectAssetId;
        SourceArtifact = sourceArtifact;
        StartingIdentity = identity;
        HasSemanticBaseline = true;
        StartingBindingIdentity = ProjectAssetIngress.BindingIdentity(BindingKind.ProjectAsset,
            projectAssetId);
    }
}

public enum ProjectAssetPublishResult
{
    Published,
    Unchanged,
}

public sealed record ExactAssetPublishResult(ProjectAssetPublishResult Result,
    string? ProjectAssetId, string? ProjectRelativeFile);

/// <summary>Normalization and semantic identity for one project-asset kind. Normalize must fully validate
/// the source and write the canonical representation to the supplied staging path.
///
/// <para><paramref name="StagedIdentity"/> is what <paramref name="SemanticIdentity"/> would answer for the
/// file <paramref name="Normalize"/> writes, where the caller ALREADY knows it — measured beside the encode
/// that produced those bytes, off the window's thread. Null is the ordinary answer: ask.</para>
///
/// <para><paramref name="ConsumesSource"/> says <paramref name="Normalize"/> leaves nothing behind it. Only
/// a transport that HANDED its bytes over may be normalized that way, and the publish refuses the pairing
/// rather than eating a return artifact a reopen would need.</para></summary>
public sealed record ProjectAssetNormalization(Action<string, string> Normalize,
    Func<string, string> SemanticIdentity, string? StagedIdentity = null, bool ConsumesSource = false);

/// <summary>Session-addressed ingress for Blender, image editors and later Import. Return artifacts are
/// distinct from canonical project files; normalization completes in same-directory staging before publish.</summary>
public static class ProjectAssetIngress
{
    public const string DirectoryName = ".ingress";

    /// <param name="handOver">This transport is opened and consumed inside ONE transaction, and it is not a
    /// round trip: the caller made <paramref name="unregisteredSource"/> for exactly this publish, no
    /// outside program is ever handed the folder, and nothing records it for a
    /// <see cref="Resume"/>. So the two snapshots a round trip lives on are not written at all — the
    /// handed-over bytes land straight on the return artifact — and the publish owes the transport no
    /// advanced baseline afterwards. The baseline it compares against is the project's own canonical file,
    /// which is the thing a snapshot would have been a copy of.
    ///
    /// <para>Every OTHER transport keeps the snapshots. They are what lets an image editor or Blender hold
    /// the file open across the app's own changes and save again into an answer that moved, which is a
    /// round trip's whole contract; a transport with no outside holder has nothing to hold.</para></param>
    public static ProjectAssetIngressSession Begin(AuthoredProject project, string editDefinitionId,
        string slotId, string? unregisteredSource = null, bool handOver = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.RootDir is null) throw new InvalidOperationException("project has no root directory");
        var edit = project.EditDefinitions.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, editDefinitionId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"edit definition '{editDefinitionId}' does not exist");
        var binding = edit.Bindings.SingleOrDefault(candidate =>
                string.Equals(candidate.SlotId, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"edit '{editDefinitionId}' has no binding for slot '{slotId}'");
        string? sourceAssetId = binding.Kind == BindingKind.ProjectAsset ? binding.ProjectAssetId : null;
        string? canonical = null;
        if (sourceAssetId is not null)
        {
            var asset = project.ProjectAssets.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, sourceAssetId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"project asset '{sourceAssetId}' does not exist");
            canonical = Resolve(project, asset.File);
            if (!File.Exists(canonical))
                throw new FileNotFoundException("project asset file is missing", canonical);
        }
        if (handOver && unregisteredSource is null)
            throw new InvalidOperationException("a handed-over transport carries its own bytes");
        string source = unregisteredSource ?? canonical
            ?? throw new InvalidOperationException("this slot has no project bytes; supply an unregistered source");
        if (!File.Exists(source)) throw new FileNotFoundException("transport source is missing", source);

        string id = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(project.RootDir, DirectoryName, Segment(editDefinitionId),
            Segment(slotId), id);
        var minted = MintDirectory(dir);
        string extension = Path.GetExtension(source);
        string sourceSnapshot = Path.Combine(dir, "source" + extension);
        string outbound = Path.Combine(dir, "outbound" + extension);
        string returned = Path.Combine(dir, "return" + extension);
        if (handOver)
        {
            File.Move(source, returned, overwrite: false);
            sourceSnapshot = canonical ?? returned;
            outbound = returned;
        }
        else
        {
            File.Copy(canonical ?? source, sourceSnapshot, overwrite: false);
            File.Copy(source, outbound, overwrite: false);
        }
        return new ProjectAssetIngressSession(id, editDefinitionId, slotId, sourceAssetId,
            sourceSnapshot, outbound, returned, canonical is null ? "" : FileIdentity(canonical),
            hasSemanticBaseline: canonical is not null, startingBindingIdentity: BindingIdentity(binding),
            mintedDirectories: minted, handedOver: handOver);
    }

    /// <summary>Reopen an app-minted transport from its exact return artifact. This is the restart path for
    /// Blender sends: the directory itself carries no semantic identity, so the caller must also provide the
    /// project-asset id and binding kind recorded when the session was launched.</summary>
    public static ProjectAssetIngressSession Resume(AuthoredProject project, string editDefinitionId,
        string slotId, string returnArtifact, string? sourceProjectAssetId,
        BindingKind? sourceBindingKind = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.RootDir is null) throw new InvalidOperationException("project has no root directory");
        string returned = Path.GetFullPath(returnArtifact);
        string expectedRoot = Path.GetFullPath(Path.Combine(project.RootDir, DirectoryName,
            Segment(editDefinitionId), Segment(slotId))) + Path.DirectorySeparatorChar;
        if (!returned.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(returned).StartsWith("return.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the return artifact is outside the addressed slot ingress");

        string directory = Path.GetDirectoryName(returned)!;
        string extension = Path.GetExtension(returned);
        string source = Path.Combine(directory, "source" + extension);
        string outbound = Path.Combine(directory, "outbound" + extension);
        if (!File.Exists(source)) throw new FileNotFoundException("transport source snapshot is missing", source);
        if (!File.Exists(outbound)) throw new FileNotFoundException("transport outbound snapshot is missing", outbound);

        var edit = project.EditDefinitions.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, editDefinitionId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"edit definition '{editDefinitionId}' does not exist");
        var binding = edit.Bindings.SingleOrDefault(candidate =>
                string.Equals(candidate.SlotId, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"edit '{editDefinitionId}' has no binding for slot '{slotId}'");
        string? expectedAsset = string.IsNullOrWhiteSpace(sourceProjectAssetId) ? null : sourceProjectAssetId;
        var expectedKind = sourceBindingKind
            ?? (expectedAsset is null ? BindingKind.TargetGameValue : BindingKind.ProjectAsset);
        string expectedBinding = BindingIdentity(expectedKind, expectedAsset);
        if (!string.Equals(BindingIdentity(binding), expectedBinding, StringComparison.Ordinal))
            throw new InvalidOperationException("the exact slot changed after this Blender transport opened");

        string identity = "";
        if (expectedAsset is not null)
        {
            var asset = project.ProjectAssets.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, expectedAsset, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"project asset '{expectedAsset}' does not exist");
            string canonical = Resolve(project, asset.File);
            if (!File.Exists(canonical)) throw new FileNotFoundException("project asset file is missing", canonical);
            identity = FileIdentity(canonical);
        }
        return new ProjectAssetIngressSession(Path.GetFileName(directory), editDefinitionId, slotId,
            expectedAsset, source, outbound, returned, identity, hasSemanticBaseline: expectedAsset is not null,
            startingBindingIdentity: expectedBinding);
    }

    internal static string BindingIdentity(Binding binding) => BindingIdentity(binding.Kind,
        binding.ProjectAssetId, binding.SourceSlot?.EditDefinitionId, binding.SourceSlot?.SlotId);

    internal static string BindingIdentity(BindingKind kind, string? projectAssetId,
        string? sourceEditDefinitionId = null, string? sourceSlotId = null) =>
        $"{(int)kind}\u001f{projectAssetId}\u001f{sourceEditDefinitionId}\u001f{sourceSlotId}";

    /// <summary>What a save coming back from an outside program says when the app moved the place it was
    /// going to land while that program held it — a revert, another edit's answer, a return from Blender.
    /// The modder's own act, and the way through it is to open the file again.</summary>
    internal const string EditMovedWhileOpen =
        "This edit changed in Doll Remolding Lab while it was open elsewhere. Open it again, then save.";

    /// <summary>The twin on the FILE rather than the place: what the outside program started from is not
    /// what the project holds any more.</summary>
    internal const string EditFileMovedWhileOpen =
        "This edit's file changed in Doll Remolding Lab while it was open elsewhere. Open it again, "
        + "then save.";

    internal static void RequireUnchangedBinding(AuthoredProject project,
        ProjectAssetIngressSession session)
    {
        var edit = project.EditDefinitions.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, session.EditDefinitionId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"edit definition '{session.EditDefinitionId}' does not exist");
        var binding = edit.Bindings.SingleOrDefault(candidate =>
                string.Equals(candidate.SlotId, session.SlotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"edit '{session.EditDefinitionId}' has no binding for slot '{session.SlotId}'");
        if (!string.Equals(BindingIdentity(binding), session.StartingBindingIdentity,
                StringComparison.Ordinal))
            throw new AuthoredRefusalException(EditMovedWhileOpen);
    }

    internal static void RequireUnchangedSource(AuthoredProject project,
        ProjectAssetIngressSession session)
    {
        if (session.SourceProjectAssetId is null) return;
        var asset = project.ProjectAssets.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, session.SourceProjectAssetId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"project asset '{session.SourceProjectAssetId}' does not exist");
        string canonical = Resolve(project, asset.File);
        if (!File.Exists(canonical) || !string.Equals(FileIdentity(canonical), session.StartingIdentity,
                StringComparison.Ordinal))
            throw new AuthoredRefusalException(EditFileMovedWhileOpen);
    }

    /// <summary>The common decoded-image normalization used by editor saves, drops and Import.</summary>
    public static ProjectAssetNormalization Png { get; } = new(
        (source, staged) => TextureIngress.Publish(source, staged),
        TextureIngress.PixelIdentity);

    /// <summary>Validated by the caller, copied byte-for-byte, and compared by content. Geometry and ramp
    /// transports use this after their format-specific reader has accepted the return.</summary>
    public static ProjectAssetNormalization Binary { get; } = new(
        (source, staged) => File.Copy(source, staged, overwrite: false),
        FileIdentity);

    /// <summary>A picture THIS APP already canonicalized, off the window's thread, whose decoded-pixel
    /// identity was measured beside the encode that produced it. Nothing is decoded, re-encoded or copied
    /// here — the bytes are MOVED into place, because decoding and re-encoding them would reproduce the
    /// very same file at the cost of the window standing still once per map, and the identity they are
    /// compared under is carried rather than re-derived.
    ///
    /// <para>The invariant it stands on: <paramref name="pixelIdentity"/> is
    /// <see cref="TextureIngress.PixelIdentity"/> of bytes written by
    /// <see cref="TextureIngress.Publish(byte[], string, bool, Action{string}?)"/> — the same canonical
    /// encoder <see cref="Png"/> runs. That is a claim only the code that DID the encoding can make, so
    /// nothing infers it: a caller passes this arm exactly for the files it produced that way and
    /// <see cref="Png"/> for every byte that came from anywhere else. The move puts the second half of the
    /// claim beyond a caller's reach as well — it is refused on any transport whose bytes were not handed
    /// over.</para></summary>
    public static ProjectAssetNormalization Prepared(string pixelIdentity)
    {
        if (string.IsNullOrWhiteSpace(pixelIdentity))
            throw new ArgumentException("prepared bytes must carry the identity measured with them",
                nameof(pixelIdentity));
        return new ProjectAssetNormalization((source, staged) => File.Move(source, staged, overwrite: false),
            TextureIngress.PixelIdentity, StagedIdentity: pixelIdentity, ConsumesSource: true);
    }

    internal static string FileIdentity(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Create <paramref name="directory"/> and hand back every level this call actually minted,
    /// DEEPEST FIRST — what a transaction that refuses has to take back out. A level that was already there
    /// belongs to somebody else and is not reported.</summary>
    internal static IReadOnlyList<string> MintDirectory(string directory)
    {
        var minted = new List<string>();
        for (string? walk = Path.GetFullPath(directory);
             walk is { Length: > 0 } && !Directory.Exists(walk);
             walk = Path.GetDirectoryName(walk))
            minted.Add(walk);
        Directory.CreateDirectory(directory);
        return minted;
    }

    internal static string Resolve(AuthoredProject project, string relative)
    {
        string root = Path.GetFullPath(project.RootDir!)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("project asset file escapes the project root");
        return resolved;
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException($"'{value}' is not a safe ingress id");
        return value;
    }
}
