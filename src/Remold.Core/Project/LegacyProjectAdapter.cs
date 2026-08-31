using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Remold.Core.Project;

public enum MigrationDisposition
{
    Inferred,
    Unresolved,
    Unsupported,
    Conflict,
}

/// <summary>One decision or problem found while reading a schema-1 project as authored intent.</summary>
public sealed record MigrationReportItem(
    string Code,
    MigrationDisposition Disposition,
    string Scope,
    string Detail)
{
    public bool BlocksSave => Disposition is
        MigrationDisposition.Unresolved or MigrationDisposition.Unsupported or MigrationDisposition.Conflict;

    /// <summary>Whether this item's <see cref="Detail"/> was written for the person reading it. Two codes
    /// file the reason exactly as the machinery worded it — a per-field validator line, the exception a
    /// part resolve threw — and neither is a sentence anyone outside the code can use. They are the whole
    /// account in the log; a refusal shown to the modder takes its cause from the items that read as
    /// sentences, and says nothing rather than saying one of these.</summary>
    public bool DetailIsForTheScreen =>
        Code is not ("intent.validation" or "identity.resolve");
}

/// <summary>The review required before a released project can be saved as schema 2. Inferences are named
/// but saveable; unresolved, unsupported or conflicting intent remains blocking.</summary>
public sealed class MigrationReport
{
    private readonly List<MigrationReportItem> _items = new();

    public IReadOnlyList<MigrationReportItem> Items => _items;
    public bool CanSave => _items.All(i => !i.BlocksSave);

    internal void Add(string code, MigrationDisposition disposition, string scope, string detail) =>
        _items.Add(new MigrationReportItem(code, disposition, scope, detail));
}

public sealed record LegacyProjectAdaptation(AuthoredProject Project, MigrationReport Report);

/// <summary>One schema-1 renderer slot re-anchored in the current install. The adapter receives this
/// structural answer rather than treating a surviving name or path id as cross-build authority.</summary>
public sealed record LegacyResolvedPart(
    TargetPart Target,
    GameAssetRef Renderer,
    GameAssetRef Mesh,
    IReadOnlyList<LegacyResolvedMaterial> Materials,
    IReadOnlyList<LegacyResolvedTier>? Tiers = null,
    IReadOnlyList<int>? MaterialIndexCounts = null);

public sealed record LegacyResolvedTier(
    string LegacyRendererSlot,
    string Tier,
    GameAssetRef Renderer,
    GameAssetRef Mesh);

public sealed record LegacyResolvedMaterial(
    int MaterialSlotIndex,
    string LegacyName,
    GameAssetRef Material,
    IReadOnlyList<LegacyResolvedTexture> Textures);

/// <summary>An exact current texture plus the schema-1 identity that reached it. The old bundle value is
/// an on-disk bundle file, so only the resolver may translate it into a logical bundle.</summary>
public sealed record LegacyResolvedTexture(
    TargetInputKind Input,
    string LegacyBundle,
    string LegacyName,
    long? LegacyPathId,
    GameAssetRef Texture,
    string? ShaderProperty = null);

/// <summary>Read-only schema-1 compatibility. The returned schema-2 project is suitable for serialization
/// only when the report permits it and structural validation succeeds.</summary>
public static class LegacyProjectAdapter
{
    /// <summary>The scope a problem with no part behind it is filed under — the whole project rather than
    /// one of its routes. The refusal an open renders reads this to tell the two apart, so the value lives
    /// here alone.</summary>
    internal const string ProjectScope = "project";

    /// <param name="rosterSlots">Every renderer slot the current install answers for on a subject. The
    /// released build derived a texture edit's reach as a live join over this roster, so an adaptation
    /// given it reads the same reach; an adaptation without it can see only the workspace's own recorded
    /// users, which is the accumulation the Edit tree badges rather than a join. Every production route
    /// passes it, paired with the same resolver as <paramref name="resolvePart"/>.</param>
    public static LegacyProjectAdaptation Adapt(ModProject legacy,
        Func<TargetPart, LegacyResolvedPart?> resolvePart,
        Func<string, string, IReadOnlyList<string>>? rosterSlots = null)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(resolvePart);
        if (legacy.Schema != ModProject.CurrentSchema)
            throw new InvalidDataException($"legacy adapter requires schema {ModProject.CurrentSchema}");

        var state = new AdapterState(legacy, resolvePart, rosterSlots);
        state.AdaptMeshes();
        state.AdaptTextures();
        state.AdaptStockRamps();
        state.AdaptHidden();
        state.FinishComposition();
        state.CompleteContentPartSlots();
        AuthoredWorkspaceNormalizer.Normalize(state.Project);
        state.ReportInvalidIntent();
        return new LegacyProjectAdaptation(state.Project, state.Report);
    }

    private sealed class AdapterState
    {
        private readonly ModProject _legacy;
        private readonly Func<TargetPart, LegacyResolvedPart?> _resolvePart;
        private readonly Func<string, string, IReadOnlyList<string>>? _rosterSlots;
        private readonly Dictionary<string, PartState> _parts = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Roster answers, one ask per subject.</summary>
        private readonly Dictionary<string, IReadOnlyList<string>> _rosters =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Roster parts resolved to answer whether they bind an edited texture, one ask each. A
        /// probe is not adaptation: it holds a null for a slot the install cannot answer for.</summary>
        private readonly Dictionary<string, LegacyResolvedPart?> _probed =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProjectAsset> _assetsByFile = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The game objects one workspace file has been minted against, per file and role — what
        /// tells a second mint of the same file that the released single-file edit has just split.</summary>
        private readonly Dictionary<string, List<GameAssetRef>> _assetsByOneFile =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ExcludedEdit> _usedExclusions = new();
        private readonly HashSet<ChangeKey> _usedKeys = new();
        private int _nextAsset = 1;
        private int _nextEdit = 1;
        private int _nextSlot = 1;
        private int _nextKeyGroup = 1;

        internal AdapterState(ModProject legacy, Func<TargetPart, LegacyResolvedPart?> resolvePart,
            Func<string, string, IReadOnlyList<string>>? rosterSlots)
        {
            _legacy = legacy;
            _resolvePart = resolvePart;
            _rosterSlots = rosterSlots;
            Project = new AuthoredProject
            {
                AppVersion = legacy.AppVersion,
                Info = CloneInfo(legacy.Info),
                AuthoredAgainst = legacy.AuthoredAgainst is null ? null : new AuthoredAgainst
                {
                    CatalogVersion = legacy.AuthoredAgainst.CatalogVersion,
                },
                RootDir = legacy.RootDir,
                WorkspaceIndex = new AuthoredWorkspaceIndex
                {
                    Selection = legacy.Selection.Select(selection => new SelectionEntry
                    {
                        Character = selection.Character, Outfit = selection.Outfit,
                    }).ToList(),
                    LegacyTargets = legacy.Targets.ToList(),
                },
            };
        }

        internal AuthoredProject Project { get; }
        internal MigrationReport Report { get; } = new();

        internal void AdaptMeshes()
        {
            foreach (var target in _legacy.Targets.Where(t => IsAssetType(t, "Mesh")))
            {
                if (!IsEdited(target)) continue;
                if (!TryPart(target, target.ObjectName, out var route)) continue;
                var part = Part(route);

                part.HasGeometry = true;
                var edit = Edit(part);
                var geometry = Asset(ProjectAssetKind.Geometry, target.ReplaceFile,
                    source: part.Resolution is null ? null : Clone(part.Resolution.Mesh));
                if (target.PathId is { } oldMesh && oldMesh != 0 && part.Resolution is { } current
                    && current.Mesh.PathId != oldMesh)
                    Report.Add("identity.moved", MigrationDisposition.Inferred, Scope(route),
                        "The game's mesh for this part changed in a game update, and the mod now "
                        + "uses the current one.");
                AddBinding(part, new TargetSlot
                {
                    Part = Clone(part.Route),
                    Tier = "lod0",
                    Input = TargetInputKind.Geometry,
                    Renderer = Renderer(part),
                    Mesh = Mesh(part, target),
                }, new Binding { Kind = BindingKind.ProjectAsset, ProjectAssetId = geometry.Id },
                    "geometry:lod0");

                int tierIndex = 0;
                foreach (var oldTier in target.LodSlots ?? Enumerable.Empty<Export.LodSlot>())
                {
                    var tier = part.Resolution?.Tiers?.SingleOrDefault(t =>
                        string.Equals(t.LegacyRendererSlot, oldTier.ObjectName, StringComparison.OrdinalIgnoreCase));
                    if (tier is null)
                    {
                        Report.Add("identity.tier", MigrationDisposition.Unresolved, Scope(route),
                            $"The '{oldTier.ObjectName}' level of detail is not in the current game "
                            + "files.");
                    }
                    else if (oldTier.PathId is { } oldPath && oldPath != 0 && tier.Mesh.PathId != oldPath)
                        Report.Add("identity.moved", MigrationDisposition.Inferred, Scope(route),
                            $"The game's mesh for '{oldTier.ObjectName}' changed in a game update, "
                            + "and the mod now uses the current one.");
                    AddBinding(part, new TargetSlot
                    {
                        Part = Clone(part.Route),
                        Tier = tier?.Tier ?? $"legacy-{++tierIndex}",
                        Input = TargetInputKind.Geometry,
                        Renderer = tier is null ? MissingRef(oldTier.ObjectName) : Clone(tier.Renderer),
                        Mesh = tier is null ? MissingRef(oldTier.ObjectName, oldTier.PathId) : Clone(tier.Mesh),
                    }, new Binding { Kind = BindingKind.ProjectAsset, ProjectAssetId = geometry.Id },
                        $"geometry:{oldTier.ObjectName}");
                }

                foreach (var row in target.DonorTextures ?? Enumerable.Empty<SubmeshTextures>())
                    AdaptDonorRow(part, edit, row, geometry);
            }
        }

        internal void AdaptTextures()
        {
            foreach (var target in _legacy.Targets.Where(t => IsAssetType(t, "Texture2D")))
            {
                if (!IsEdited(target)) continue;
                // The subject only: which of its parts the edit reaches is the join below, never the
                // renderer slot this call needs an argument for.
                if (!TryPart(target, target.ObjectName, out var subject)) continue;

                foreach (var part in EditedTextureParts(target, subject))
                {
                    var route = part.Route;
                    if (part.Resolution is null)
                    {
                        // The edit is still explicit; keep its file while the already-reported identity
                        // gap blocks migration. Only a successfully resolved route can prove it stale.
                        Asset(ProjectAssetKind.Picture, target.ReplaceFile);
                        continue;
                    }
                    if (part.HasGeometry)
                    {
                        // The released build gave a part it replaces no retexture — the replacement's own
                        // donor maps dress those draws — and named the edit it dropped. Same outcome, and
                        // named here too rather than skipped in silence.
                        Report.Add("texture.replaced", MigrationDisposition.Inferred, Scope(route),
                            $"The edited texture '{target.ObjectName}' is not used on a part this mod "
                            + "replaces, because the replacement's own maps cover it.");
                        continue;
                    }
                    var uses = part.Resolution.Materials
                        .SelectMany(m => m.Textures
                            .Where(t => Matches(target, t))
                            .Select(t => (Material: m, Texture: t)))
                        .ToList();
                    if (uses.Count == 0)
                    {
                        Report.Add("texture.binding", MigrationDisposition.Inferred, Scope(route),
                            $"No material on this part uses '{target.ObjectName}' any more, so that "
                            + "edited texture was dropped.");
                        continue;
                    }

                    foreach (var use in uses)
                    {
                        if (use.Texture.Input is not (TargetInputKind.BaseColor
                            or TargetInputKind.Normal or TargetInputKind.Rmo))
                        {
                            Report.Add("texture.input", MigrationDisposition.Inferred, Scope(route),
                                $"'{target.ObjectName}' is used here as the "
                                + $"{Textures.TextureMap.SlotLabel(use.Texture.Input, null)}, which this "
                                + "mod never changed, so that edit was dropped.");
                            continue;
                        }
                        // One released file replacing SEVERAL current textures is one project asset per
                        // object it stands in for: schema 1 overrode the recorded identity wherever this
                        // install binds it, and each of those is its own provenance.
                        var picture = Asset(ProjectAssetKind.Picture, target.ReplaceFile,
                            use.Texture.Texture);
                        part.HasTexture = true;
                        Edit(part);
                        RequireExact(use.Material.Material, Scope(route),
                            $"Material {use.Material.MaterialSlotIndex}");
                        RequireExact(use.Texture.Texture, Scope(route),
                            $"The {Textures.TextureMap.SlotLabel(use.Texture.Input, use.Texture.ShaderProperty)}");
                        if (target.PathId is { } oldPath && oldPath != 0
                            && use.Texture.Texture.PathId != oldPath)
                            Report.Add("identity.moved", MigrationDisposition.Inferred, Scope(route),
                                $"The game's '{target.ObjectName}' texture changed in a game update, "
                                + "and the mod now uses the current one.");
                        AddBinding(part, new TargetSlot
                        {
                            Part = Clone(part.Route),
                            SubmeshIndex = use.Material.MaterialSlotIndex,
                            MaterialSlotIndex = use.Material.MaterialSlotIndex,
                            Input = use.Texture.Input,
                            Renderer = Renderer(part),
                            Mesh = part.Resolution is null ? null : Clone(part.Resolution.Mesh),
                            Material = Clone(use.Material.Material),
                        }, new Binding { Kind = BindingKind.ProjectAsset, ProjectAssetId = picture.Id },
                            $"game:{use.Material.MaterialSlotIndex}:{use.Texture.Input}");
                    }
                }
            }
        }

        /// <summary>The parts an edited released texture retextures. The released build derived this as a
        /// LIVE JOIN — every part of the subject's current roster whose materials bind the texture — and
        /// never read <see cref="ProjectTarget.Users"/>, which is the workspace's own accumulation behind
        /// the Edit tree's nesting badge. Walking the recorded users instead loses a retexture the released
        /// build shipped wherever that accumulation missed a part binding the same map.
        ///
        /// <para>Without a roster to ask, the recorded users are the only reach this adaptation can see, and
        /// the walk falls back to them.</para></summary>
        private IEnumerable<PartState> EditedTextureParts(ProjectTarget target, TargetPart subject)
        {
            if (_rosterSlots is null) return RecordedUserParts(target, subject);
            var roster = Roster(subject);
            if (roster.Count == 0)
            {
                // The install answers for no part of the subject, so nothing says where this edit lands.
                // Named and omitted rather than blocking: the released derivation refused an unresolved
                // subject only where it carried a MESH edit or a hide and passed a texture-only one by,
                // and a workspace holds texture targets for subjects it never selected at all.
                Report.Add("texture.subject", MigrationDisposition.Inferred,
                    $"{subject.Subject} / {subject.Outfit}",
                    "This outfit has no parts in the current game files, so its edited texture "
                    + $"'{target.ObjectName}' was dropped.");
                return Array.Empty<PartState>();
            }
            var found = new List<PartState>();
            foreach (string slot in roster)
            {
                var route = new TargetPart
                {
                    Subject = subject.Subject, Outfit = subject.Outfit, RendererSlot = slot,
                };
                if (Probe(route) is not { } resolution) continue;
                if (!resolution.Materials.Any(m => m.Textures.Any(t => Matches(target, t)))) continue;
                found.Add(Part(route));
            }
            if (found.Count == 0)
                Report.Add("texture.binding", MigrationDisposition.Inferred,
                    $"{subject.Subject} / {subject.Outfit}",
                    $"No material on this outfit uses '{target.ObjectName}' any more, so that edited "
                    + "texture was dropped.");
            return found;
        }

        /// <summary>The recorded-users walk, for an adaptation with no roster to join against.</summary>
        private IEnumerable<PartState> RecordedUserParts(ProjectTarget target, TargetPart subject)
        {
            if (target.Users is not { Count: > 0 })
            {
                Report.Add("texture.users", MigrationDisposition.Inferred, target.ObjectName,
                    "This edited texture is not used by any part, so it was dropped.");
                yield break;
            }
            foreach (string slot in target.Users.Distinct(StringComparer.OrdinalIgnoreCase))
                yield return Part(new TargetPart
                {
                    Subject = subject.Subject, Outfit = subject.Outfit, RendererSlot = slot,
                });
        }

        /// <summary>The install's parts for one subject, asked once.</summary>
        private IReadOnlyList<string> Roster(TargetPart subject)
        {
            string key = $"{subject.Subject}\u001f{subject.Outfit}";
            if (_rosters.TryGetValue(key, out var cached)) return cached;
            IReadOnlyList<string> slots;
            try { slots = _rosterSlots!(subject.Subject, subject.Outfit) ?? Array.Empty<string>(); }
            catch (Exception e)
            {
                Report.Add("identity.roster", MigrationDisposition.Unresolved,
                    $"{subject.Subject} / {subject.Outfit}",
                    $"Couldn't read this outfit's parts from the game files: {e.Message}");
                slots = Array.Empty<string>();
            }
            _rosters[key] = slots;
            return slots;
        }

        /// <summary>The install's answer for a roster slot this project may never have edited, asked once.
        /// A probe reports no identity of its own: only a part that proves it binds an edited texture joins
        /// the conversion, and <see cref="Part"/> asks for exactness then.</summary>
        private LegacyResolvedPart? Probe(TargetPart route)
        {
            if (_parts.TryGetValue(route.Key, out var adapted)) return adapted.Resolution;
            if (_probed.TryGetValue(route.Key, out var cached)) return cached;
            LegacyResolvedPart? resolution = null;
            try { resolution = _resolvePart(Clone(route)); }
            catch (Exception e)
            {
                // Named, not swallowed: a part the install can't be asked about may be one the edit
                // belongs on, and the modder is the only one who can tell.
                Report.Add("identity.probe", MigrationDisposition.Inferred, Scope(route),
                    $"Couldn't check whether this part uses an edited texture: {e.Message}");
            }
            if (resolution is not null && !resolution.Target.SameAs(route)) resolution = null;
            _probed[route.Key] = resolution;
            return resolution;
        }

        internal void AdaptStockRamps()
        {
            foreach (var pick in _legacy.StockRamps ?? Enumerable.Empty<StockRampPick>())
            {
                var route = new TargetPart
                {
                    Subject = pick.Character,
                    Outfit = pick.Outfit,
                    RendererSlot = pick.Mesh,
                };
                var part = Part(route);
                if (part.HasGeometry)
                {
                    // The released build gave a part it replaces no picked ramp — the replacement carries
                    // its own — and named the pick it dropped. Same outcome, named here instead, because
                    // the combination is one only the released shape can hold: a replaced part's ramp is
                    // its replacement's own slot, and a build that met both would refuse.
                    Report.Add("ramp.replaced", MigrationDisposition.Inferred, Scope(route),
                        $"The toon ramp '{Path.GetFileName(pick.Ramp)}' was picked on a part this mod "
                        + "replaces, so it was dropped. The replacement has its own toon ramp.");
                    continue;
                }
                part.HasRamp = true;
                var edit = Edit(part);
                var ramp = Asset(ProjectAssetKind.Ramp, pick.Ramp);
                var materials = part.Resolution?.Materials.Where(m =>
                    string.Equals(m.LegacyName, pick.Material, StringComparison.OrdinalIgnoreCase)).ToList()
                    ?? new List<LegacyResolvedMaterial>();
                if (materials.Count != 1)
                {
                    Report.Add("ramp.material", materials.Count == 0
                            ? MigrationDisposition.Unresolved : MigrationDisposition.Conflict,
                        Scope(route), materials.Count == 0
                            ? $"The material '{pick.Material}' the toon ramp was picked on is not in the "
                              + "current game files."
                            : $"This part has {materials.Count} materials named '{pick.Material}', so the "
                              + "toon ramp pick cannot be placed.");
                }
                var material = materials.SingleOrDefault();
                if (material is not null)
                    RequireExact(material.Material, Scope(route),
                        $"Material {material.MaterialSlotIndex}");
                AddBinding(part, new TargetSlot
                {
                    Part = Clone(part.Route),
                    SubmeshIndex = material?.MaterialSlotIndex,
                    MaterialSlotIndex = material?.MaterialSlotIndex,
                    Input = TargetInputKind.Ramp,
                    Renderer = Renderer(part),
                    Mesh = part.Resolution is null ? null : Clone(part.Resolution.Mesh),
                    Material = material is null ? null : Clone(material.Material),
                }, new Binding { Kind = BindingKind.ProjectAsset, ProjectAssetId = ramp.Id },
                    $"stock-ramp:{pick.Material}");
                Report.Add("ramp.source", MigrationDisposition.Inferred, Scope(route),
                    $"The picked toon ramp '{Path.GetFileName(pick.Ramp)}' does not say which original "
                    + "ramp it was made from. The file itself is kept.");
            }
        }

        internal void AdaptHidden()
        {
            foreach (var hidden in _legacy.Hidden)
            {
                var route = new TargetPart
                {
                    Subject = hidden.Character,
                    Outfit = hidden.Outfit,
                    RendererSlot = hidden.Mesh,
                };
                var part = Part(route, MigrationDisposition.Inferred, "hidden.stale");
                if (part.Resolution is null) continue;
                part.Hidden = true;
                BoundHideEdit(part);
            }
        }

        internal void FinishComposition()
        {
            var keyed = new List<KeyedEdit>();
            foreach (var part in _parts.Values)
            {
                var activeEdit = part.Hidden ? part.HideEdit : part.Edit;
                if (activeEdit is null) continue;
                string verb = part.Hidden ? EditVerbs.Hide
                    : part.HasGeometry ? EditVerbs.Replace
                    : part.HasTexture ? EditVerbs.Retexture
                    : EditVerbs.Ramp;

                var exclusions = _legacy.BuildExcluded.Where(x => IsFor(x, part.Route, verb)).ToList();
                if (exclusions.Count > 0)
                {
                    foreach (var exclusion in exclusions) _usedExclusions.Add(exclusion);
                    if (exclusions.Count > 1)
                        Report.Add("build.duplicate_exclusion", MigrationDisposition.Conflict, Scope(part.Route),
                            $"{exclusions.Count} entries turn this part's {verb} change off, and the "
                            + "mod cannot tell which one to keep.");
                    // A released unticked row keeps its edit but gives it no activation placement.
                    continue;
                }

                var keys = _legacy.ChangeKeys.Where(k => IsFor(k, part.Route, verb)).ToList();
                string? key = null;
                bool startsOff = false;
                bool hideWhenOff = false;
                if (keys.Count > 0)
                {
                    foreach (var setting in keys) _usedKeys.Add(setting);
                    if (keys.Count > 1)
                        Report.Add("build.duplicate_key", MigrationDisposition.Conflict, Scope(part.Route),
                            $"{keys.Count} keys are set on this part's {verb} change, and the mod "
                            + "cannot tell which one to keep.");
                    var activeKey = keys[0];
                    string? normalized = ModKeys.Normalize(activeKey.Key);
                    if (normalized is null)
                        Report.Add("build.key", MigrationDisposition.Unsupported, Scope(part.Route),
                            $"{activeKey.Key} cannot be used as a toggle key.");
                    else
                    {
                        bool mayHide = verb == EditVerbs.Replace;
                        if (activeKey.HideWhenOff && !mayHide)
                            Report.Add("build.off_state", MigrationDisposition.Inferred, Scope(part.Route),
                                $"A {verb} change cannot hide the part while its key is off, so the "
                                + "part returns to the original instead.");
                        key = normalized;
                        startsOff = activeKey.StartsOff;
                        hideWhenOff = activeKey.HideWhenOff && mayHide;
                    }
                }

                if (key is null) Project.Always.Add(activeEdit.Id);
                else
                {
                    string? offEditId = hideWhenOff ? BoundHideEdit(part).Id : null;
                    keyed.Add(new KeyedEdit(activeEdit.Id, Clone(part.Route), key, startsOff, offEditId));
                }
            }
            AddKeyGroups(keyed);

            foreach (var exclusion in _legacy.BuildExcluded.Where(x => !_usedExclusions.Contains(x)))
                Report.Add("build.inactive_exclusion", MigrationDisposition.Inferred,
                    $"{exclusion.Character} / {exclusion.Outfit} / {exclusion.Mesh}",
                    $"An off switch was recorded for a {exclusion.Verb} change this mod no longer has, "
                    + "and was dropped.");
            foreach (var key in _legacy.ChangeKeys.Where(k => !_usedKeys.Contains(k)))
                Report.Add("build.inactive_key", MigrationDisposition.Inferred,
                    $"{key.Character} / {key.Outfit} / {key.Mesh}",
                    $"A key was recorded for a {key.Verb} change this mod no longer has, and was "
                    + "dropped.");
        }

        /// <summary>File every installed game route for each adapted content edit, through the same
        /// part-slot path used when the edit page adds an edit.</summary>
        internal void CompleteContentPartSlots()
        {
            foreach (var part in _parts.Values)
            {
                if (part.Edit is null || part.Resolution is null) continue;
                try
                {
                    AuthoredEditSession.EnsurePartSlots(Project, part.Route, part.Resolution);
                }
                catch (InvalidOperationException e)
                {
                    Report.Add("identity.exact", MigrationDisposition.Unresolved,
                        Scope(part.Route), e.Message);
                }
            }
        }

        /// <summary>One key becomes one two-state group over every change bound to it: state 0 is what those
        /// parts show while it is on, state 1 what each returns to while it is off. Schema 1 stored the same
        /// facts per change, so the disagreements it allowed between changes sharing a key are resolved here
        /// once rather than left to the emitter.</summary>
        private void AddKeyGroups(IReadOnlyList<KeyedEdit> keyed)
        {
            foreach (var group in keyed.GroupBy(member => member.Key, StringComparer.Ordinal))
            {
                var members = group.ToList();
                string scope = string.Join("; ", members.Select(member => Scope(member.Target)));

                // One key is ONE emitted switch with one start, so a shared key starts off only when every
                // change on it asks to. This is the released emitter's own resolution, named here because
                // the group records a single answer where the changes each recorded their own.
                //
                // The mod's OWN key is the whole-mod switch and launches ON whatever a change bound to it
                // asked: the mod gate reads that one variable at its first position, so a group launching
                // anywhere else on it would close the mod over its own content. That is the released
                // resolution too, and it is silent there — the change row's own mark says the outcome.
                bool startsOff = members.All(m => m.StartsOff)
                    && !ModKeys.SameKey(group.Key, ModKeys.Normalize(_legacy.Info.ToggleKey));
                if (members.Select(m => m.StartsOff).Distinct().Count() > 1)
                    Report.Add("build.key_start", MigrationDisposition.Inferred, scope,
                        $"Changes on key {group.Key} disagreed about starting off. One key is one "
                        + "switch, so they all start on.");

                var active = members.Select(member => member.EditId).ToList();
                var off = members.Where(member => member.OffEditId is not null)
                    .Select(member => member.OffEditId!).ToList();
                if (startsOff) (active, off) = (off, active);
                Project.KeyGroups.Add(new KeyGroup
                {
                    Id = $"key-{_nextKeyGroup++:D4}",
                    Key = group.Key,
                    States = new List<KeyGroupState>
                    {
                        new() { Id = "state-0001", ActiveEditIds = active },
                        new() { Id = "state-0002", ActiveEditIds = off },
                    },
                });
            }
        }

        internal void ReportInvalidIntent()
        {
            foreach (string error in AuthoredProjectValidator.Errors(Project))
                Report.Add("intent.validation", MigrationDisposition.Unresolved, ProjectScope, error);
        }

        private void AdaptDonorRow(PartState part, EditDefinition edit, SubmeshTextures row,
            ProjectAsset geometry)
        {
            bool reliefDue = row.AlbedoAsk.IsAsk() || row.NormalAsk.IsAsk() || row.RmoAsk.IsAsk();
            AddDonorMap(part, edit, row, TargetInputKind.BaseColor, row.Albedo, row.AlbedoAsk,
                implicitNeutral: false, reliefDue);
            AddDonorMap(part, edit, row, TargetInputKind.Normal, row.Normal, row.NormalAsk,
                implicitNeutral: true, reliefDue);
            var rmo = AddDonorMap(part, edit, row, TargetInputKind.Rmo, row.Rmo, row.RmoAsk,
                implicitNeutral: true, reliefDue);
            // Blend was added after the released four-field donor layout. An absent additive field is not an
            // implicit legacy slot: only adapt it when the old row actually records a Blend decision or picture.
            if (row.Blend is not null || row.BlendAsk.IsAsk())
                AddDonorMap(part, edit, row, TargetInputKind.Blend, row.Blend, row.BlendAsk,
                    implicitNeutral: false, reliefDue: false);
            foreach (var property in row.Textures ?? new List<PropertyTextureBinding>())
                AddDonorMap(part, edit, row, TargetInputKind.Texture, property.File, property.Ask,
                    implicitNeutral: false, reliefDue: false, shaderProperty: property.ShaderProperty);
            AddDonorMap(part, edit, row, TargetInputKind.Ramp, row.Ramp, row.RampAsk,
                implicitNeutral: false, reliefDue: false, carried: row.RampCarried);

            if (rmo is null) return;
            var alpha = Asset(ProjectAssetKind.StructuredValue, row.Rmo!, sourceAssetId: rmo.Id,
                distinctRole: "rmo-alpha");
            alpha.Value = new ProjectAssetValue
            {
                Semantic = "rmo-alpha",
                Value = row.RmoAlpha switch
                {
                    RmoAlphaAnswer.Rebuild => "rebuild-from-stock",
                    RmoAlphaAnswer.ShipAsAuthored => "ship-as-authored",
                    _ => "unrecorded",
                },
            };
            string explanation = row.RmoAlpha switch
            {
                RmoAlphaAnswer.Rebuild => "The emissive mask was rebuilt from the original map.",
                RmoAlphaAnswer.ShipAsAuthored => "The RMO's own emissive mask was kept.",
                _ => "The RMO file is kept, but the mod did not record which emissive mask to use.",
            };
            if (row.RmoAlpha is null)
                Report.Add("rmo_alpha.absent", MigrationDisposition.Inferred,
                    $"{Scope(part.Route)} / submesh {row.Submesh}", explanation);
            AddBinding(part, DonorSlot(part, edit, row.Submesh, TargetInputKind.RmoAlpha),
                new Binding { Kind = BindingKind.ProjectAsset, ProjectAssetId = alpha.Id },
                $"donor:{row.Submesh}:rmo-alpha");
        }

        private ProjectAsset? AddDonorMap(PartState part, EditDefinition edit, SubmeshTextures row,
            TargetInputKind input, string? file, SlotOrigin origin, bool implicitNeutral, bool reliefDue,
            CarriedRamp? carried = null, string? shaderProperty = null)
        {
            ProjectAsset? asset = null;
            Binding binding;
            if (file is not null)
            {
                var source = carried is null ? null : FindCarriedRamp(part, carried);
                asset = Asset(input == TargetInputKind.Ramp ? ProjectAssetKind.Ramp : ProjectAssetKind.Picture,
                    file, source);
                binding = new Binding { Kind = BindingKind.ProjectAsset, ProjectAssetId = asset.Id };
            }
            else if (origin == SlotOrigin.ExplicitNeutral)
            {
                if (input is not (TargetInputKind.Normal or TargetInputKind.Rmo))
                {
                    Report.Add("slot.neutral", MigrationDisposition.Unsupported,
                        $"{Scope(part.Route)} / submesh {row.Submesh}",
                        $"The {Textures.TextureMap.SlotLabel(input, shaderProperty)} cannot be set to a "
                        + "neutral value.");
                    binding = new Binding { Kind = BindingKind.InheritedLiveCarrier };
                }
                else binding = new Binding { Kind = BindingKind.Neutral };
            }
            else if (origin == SlotOrigin.VanillaOwn)
                binding = (input == TargetInputKind.Ramp
                    ? KeepOwnRampBinding(part, edit, row.Submesh) : null)
                    ?? new Binding { Kind = BindingKind.InheritedLiveCarrier };
            else if (implicitNeutral && reliefDue)
            {
                binding = new Binding { Kind = BindingKind.Neutral };
                Report.Add("slot.implicit_neutral", MigrationDisposition.Inferred,
                    $"{Scope(part.Route)} / submesh {row.Submesh}",
                    $"The {Textures.TextureMap.SlotLabel(input, shaderProperty)} was left empty and "
                    + "another image on this submesh was edited, so it becomes a neutral map.");
            }
            else binding = new Binding { Kind = BindingKind.InheritedLiveCarrier };

            AddBinding(part, DonorSlot(part, edit, row.Submesh, input, shaderProperty), binding,
                $"donor:{row.Submesh}:{shaderProperty ?? input.ToString()}");
            return asset;
        }

        /// <summary>What a donor row's recorded keep-the-game's-ramp becomes: a source slot naming the part's
        /// own installed toon ramp at that material position, addressed with no edit definition of its own —
        /// the project asking the installed game by address. That is what tells the decision apart from a ramp
        /// slot nobody has answered yet, which inherits the live carrier and stays a question the conversion
        /// may still fill. The game slot asks for the game's own value, so the two records say one thing.
        ///
        /// <para>Null where this install names no such place, and the caller leaves the slot unanswered. A
        /// keep decision presupposes a ramp card, which presupposes the material's ramp input, so a row
        /// carrying one without it is legacy data the released surface could not have produced; it is named
        /// as an omitted inference rather than answered by minting a slot the game would never read. A
        /// material a stock-ramp pick already answers is left alone for the same reason: the pick answers that
        /// game slot, and pointing the row at it would turn a keep decision into the pick's file.</para>
        /// </summary>
        private Binding? KeepOwnRampBinding(PartState part, EditDefinition edit, int submesh)
        {
            string scope = $"{Scope(part.Route)} / submesh {submesh}";
            var materials = part.Resolution?.Materials
                .Where(candidate => candidate.MaterialSlotIndex == submesh).ToList()
                ?? new List<LegacyResolvedMaterial>();
            var material = materials.Count == 1 ? materials[0] : null;
            if (material is null
                || material.Textures.All(texture => texture.Input != TargetInputKind.Ramp))
            {
                Report.Add("ramp.keep_own", MigrationDisposition.Inferred, scope,
                    "This material draws without a toon ramp in the current game files, so the recorded "
                    + "keep-the-original-ramp choice was dropped.");
                return null;
            }
            if ((_legacy.StockRamps ?? Enumerable.Empty<StockRampPick>()).Any(pick =>
                    pick.IsForSubject(part.Route.Subject, part.Route.Outfit)
                    && string.Equals(pick.Mesh, part.Route.RendererSlot, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pick.Material, material.LegacyName, StringComparison.OrdinalIgnoreCase)))
            {
                Report.Add("ramp.keep_own", MigrationDisposition.Inferred, scope,
                    $"A picked toon ramp already answers material '{material.LegacyName}', so the "
                    + "recorded keep-the-original-ramp choice was dropped.");
                return null;
            }

            RequireExact(material.Material, Scope(part.Route),
                $"Material {material.MaterialSlotIndex}");
            string key = $"game:{material.MaterialSlotIndex}:{TargetInputKind.Ramp}";
            AddBinding(part, new TargetSlot
            {
                Part = Clone(part.Route),
                SubmeshIndex = material.MaterialSlotIndex,
                MaterialSlotIndex = material.MaterialSlotIndex,
                Input = TargetInputKind.Ramp,
                Renderer = Renderer(part),
                Mesh = part.Resolution is null ? null : Clone(part.Resolution.Mesh),
                Material = Clone(material.Material),
            }, new Binding { Kind = BindingKind.TargetGameValue }, key, edit);
            return new Binding
            {
                Kind = BindingKind.SourceSlot,
                SourceSlot = new BindingSourceSlot { SlotId = part.Slots[key].Id },
            };
        }

        private TargetSlot DonorSlot(PartState part, EditDefinition edit, int submesh, TargetInputKind input,
            string? shaderProperty = null) =>
            new()
            {
                Part = Clone(part.Route),
                SubmeshIndex = submesh,
                MaterialSlotIndex = submesh,
                Input = input,
                ShaderProperty = shaderProperty,
                Domain = TargetSlotDomain.EditOutput,
                Renderer = Renderer(part),
                Mesh = part.Resolution is null ? null : Clone(part.Resolution.Mesh),
            };

        private GameAssetRef? FindCarriedRamp(PartState part, CarriedRamp carried)
        {
            // Two materials of one part binding ONE texture reach it twice, and that is one game object,
            // not an ambiguity — so the lineage is judged on the OBJECTS reached, not on how many bindings
            // reached them.
            var matches = part.Resolution?.Materials.SelectMany(m => m.Textures)
                .Where(t => string.Equals(t.LegacyBundle, carried.Bundle, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.LegacyName, carried.Name, StringComparison.OrdinalIgnoreCase)
                    && (carried.PathId is null || t.LegacyPathId == carried.PathId))
                .Select(t => t.Texture).DistinctBy(GameKey).ToList() ?? new List<GameAssetRef>();
            if (matches.Count == 1) return Clone(matches[0]);
            // A ramp carried across from ANOTHER subject's material is not among this part's own, and the
            // row already names the object it was taken off by bundle and path id — which IS the identity,
            // read live when the conversion carried it. Re-anchoring only ever had to fill in what the row
            // left out.
            if (carried.PathId is { } pathId && pathId != 0
                && !string.IsNullOrWhiteSpace(carried.Bundle))
                return new GameAssetRef
                {
                    GameBuild = part.Resolution?.Renderer.GameBuild
                        ?? _legacy.AuthoredAgainst?.CatalogVersion ?? "",
                    LogicalBundle = carried.Bundle,
                    PathId = pathId,
                    Name = carried.Name,
                };
            Report.Add("ramp.lineage", MigrationDisposition.Inferred, Scope(part.Route),
                $"The toon ramp '{carried.Name}' does not match one original ramp in the current game "
                + "files. Its file in the mod is kept as is.");
            return null;
        }

        private PartState Part(TargetPart route,
            MigrationDisposition missingDisposition = MigrationDisposition.Unresolved,
            string missingCode = "identity.part")
        {
            if (_parts.TryGetValue(route.Key, out var found)) return found;
            LegacyResolvedPart? resolution = null;
            try { resolution = _resolvePart(Clone(route)); }
            catch (Exception e)
            {
                Report.Add("identity.resolve", missingDisposition, Scope(route), e.Message);
            }
            if (resolution is null || !resolution.Target.SameAs(route))
            {
                Report.Add(missingCode, missingDisposition, Scope(route),
                    missingDisposition == MigrationDisposition.Inferred
                        ? "This part is no longer in the game files, and the entry was dropped."
                        : "This part is not in the current game files.");
                resolution = null;
            }
            else
            {
                RequireExact(resolution.Renderer, Scope(route), "This part");
                RequireExact(resolution.Mesh, Scope(route), "This part's mesh");
            }
            found = new PartState(Clone(route), resolution);
            _parts.Add(route.Key, found);
            return found;
        }

        private bool TryPart(ProjectTarget target, string rendererSlot, out TargetPart part)
        {
            string? subject = target.SubjectCharacter;
            string? outfit = target.SubjectOutfit;
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(outfit))
            {
                if (_legacy.Selection.Count == 1)
                {
                    subject = _legacy.Selection[0].Character;
                    outfit = _legacy.Selection[0].Outfit;
                    Report.Add("identity.subject", MigrationDisposition.Inferred, target.ObjectName,
                        $"This edit did not say which outfit it belongs to, and was read as {subject} / "
                        + $"{outfit}, the mod's only one.");
                }
                else
                {
                    Report.Add("identity.subject", MigrationDisposition.Unresolved, target.ObjectName,
                        "This edit does not say which outfit it belongs to, and the mod has more than "
                        + "one.");
                    part = new TargetPart();
                    return false;
                }
            }
            part = new TargetPart { Subject = subject!, Outfit = outfit!, RendererSlot = rendererSlot };
            return true;
        }

        private bool IsEdited(ProjectTarget target)
        {
            string scope = target.ObjectName;
            if (target.OriginalFile is null)
            {
                Report.Add("edit.original_absent", MigrationDisposition.Inferred, scope,
                    "No original file was recorded, so this is kept as an edit.");
                return true;
            }
            if (_legacy.RootDir is null)
            {
                Report.Add("edit.flag", MigrationDisposition.Inferred, scope,
                    $"The mod's files could not be read, so the recorded edited state ({target.Edited}) "
                    + "was used.");
                return target.Edited;
            }
            try
            {
                string replacement = _legacy.Resolve(target.ReplaceFile);
                string original = _legacy.Resolve(target.OriginalFile);
                if (!File.Exists(replacement) || !File.Exists(original))
                {
                    Report.Add("edit.file_absent", MigrationDisposition.Unresolved, scope,
                        "A file this edit needs is missing from the mod folder, so the app cannot tell "
                        + "whether it was edited.");
                    return true;
                }
                return !FilesEqual(replacement, original);
            }
            catch (Exception e)
            {
                Report.Add("edit.compare", MigrationDisposition.Inferred, scope,
                    $"Couldn't compare this edit with the original, so it is kept as an edit: "
                    + $"{e.Message}");
                return true;
            }
        }

        private EditDefinition Edit(PartState part)
        {
            if (part.Edit is not null) return part.Edit;
            part.Edit = new EditDefinition
            {
                Id = $"edit-{_nextEdit++:D4}",
                Target = Clone(part.Route),
                Label = AuthoredEditSession.NewEditLabel(Project, part.Route, null),
            };
            Project.EditDefinitions.Add(part.Edit);
            return part.Edit;
        }

        private EditDefinition HideEdit(PartState part)
        {
            if (part.HideEdit is not null) return part.HideEdit;
            part.HideEdit = new EditDefinition
            {
                Id = $"edit-{_nextEdit++:D4}",
                Kind = EditDefinitionKind.Hide,
                Target = Clone(part.Route),
                Label = "Hidden",
            };
            Project.EditDefinitions.Add(part.HideEdit);
            return part.HideEdit;
        }

        private EditDefinition BoundHideEdit(PartState part)
        {
            var hide = HideEdit(part);
            if (hide.Bindings.Count > 0) return hide;
            AddBinding(part, new TargetSlot
            {
                Part = Clone(part.Route),
                Input = TargetInputKind.Visibility,
                Renderer = Renderer(part),
                Mesh = Clone(part.Resolution!.Mesh),
            }, new Binding { Kind = BindingKind.Hidden }, "hide:visibility", hide);
            return hide;
        }

        private ProjectAsset Asset(ProjectAssetKind kind, string file, GameAssetRef? source = null,
            string? sourceAssetId = null, string distinctRole = "")
        {
            // ONE workspace file can stand behind two different game objects — a picture replacing
            // two stock textures, a donor glb replacing two parts — and each of those is its own
            // project asset with its own provenance. Identity is therefore the file AND what it was
            // made from, not the file alone.
            string key = $"{kind}\u001f{distinctRole}\u001f{file}\u001f{SourceKey(source)}\u001f{sourceAssetId}";
            if (_assetsByFile.TryGetValue(key, out var existing))
            {
                // The key carries exactly the identity Same() compares, so a hit answers for the same game
                // object by construction. Asserted rather than reconciled: a key that stopped carrying the
                // source would silently merge two objects' provenance into one asset.
                Debug.Assert(source is null || existing.Source?.GameAsset is not { } held
                    || Same(held, source), "a cached project asset answered for a different game source");
                return existing;
            }
            string label = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(label)) label = kind.ToString();
            var asset = new ProjectAsset
            {
                Id = $"asset-{_nextAsset++:D4}",
                Kind = kind,
                Label = label,
                File = file,
                Source = source is not null ? new ProjectAssetSource { GameAsset = Clone(source) }
                    : sourceAssetId is not null ? new ProjectAssetSource { ProjectAssetId = sourceAssetId }
                    : null,
            };
            _assetsByFile.Add(key, asset);
            ReportSplitFile(kind, file, distinctRole, source);
            Project.ProjectAssets.Add(asset);
            return asset;
        }

        /// <summary>Names the split when one released file mints a SECOND asset because it stands behind a
        /// second game object. Schema 1 overrode that file wherever the install bound it, so re-saving the
        /// picture changed every object at once; schema 2 gives each object an asset of its own, and a later
        /// edit of one leaves the others on the bytes they hold. What the modder's NEXT edit does changes,
        /// so the conversion says it rather than splitting quietly.</summary>
        private void ReportSplitFile(ProjectAssetKind kind, string file, string distinctRole,
            GameAssetRef? source)
        {
            // Only a mint that names a GAME OBJECT splits this way. A second asset over one file carrying a
            // project-asset lineage instead (an authored value beside its own picture) is a second record
            // about one object, not a second object.
            if (source is null) return;
            string fileKey = $"{kind}\u001f{distinctRole}\u001f{file}";
            if (!_assetsByOneFile.TryGetValue(fileKey, out var kin))
            {
                _assetsByOneFile[fileKey] = new List<GameAssetRef> { source };
                return;
            }
            kin.Add(source);
            Report.Add("asset.split", MigrationDisposition.Inferred, file,
                $"This one file was used for {kin.Count} different game textures "
                + $"({string.Join(", ", kin.Select(ObjectLabel))}). Each becomes its own "
                + $"{kind.ToString().ToLowerInvariant()} in the mod, so editing one no longer changes "
                + "the others.");
        }

        private static string ObjectLabel(GameAssetRef source) =>
            string.IsNullOrWhiteSpace(source.Name) ? "an unnamed texture" : $"'{source.Name}'";

        private static string SourceKey(GameAssetRef? source) => source is null ? ""
            : $"{source.GameBuild}|{source.LogicalBundle}|{source.PathId}";

        private void AddBinding(PartState part, TargetSlot slot, Binding binding, string key,
            EditDefinition? selectedEdit = null)
        {
            var edit = selectedEdit ?? Edit(part);
            var target = EnsureSlot(part, slot, key);
            binding.SlotId = target.Id;
            var existing = edit.Bindings.FirstOrDefault(b => b.SlotId == target.Id);
            if (existing is null) edit.Bindings.Add(binding);
            else if (!Same(existing, binding))
                Report.Add("binding.conflict", MigrationDisposition.Conflict, Scope(part.Route),
                    "Two different values were recorded for the same map on this part, and the mod "
                    + "cannot tell which one to keep.");
        }

        private TargetSlot EnsureSlot(PartState part, TargetSlot slot, string key)
        {
            if (part.Slots.TryGetValue(key, out var existing)) return existing;
            slot.Id = $"slot-{_nextSlot++:D4}";
            part.Slots.Add(key, slot);
            Project.TargetSlots.Add(slot);
            return slot;
        }

        private GameAssetRef Renderer(PartState part) => part.Resolution is null
            ? MissingRef(part.Route.RendererSlot)
            : Clone(part.Resolution.Renderer);

        private GameAssetRef Mesh(PartState part, ProjectTarget old) => part.Resolution is null
            ? MissingRef(old.ObjectName, old.PathId)
            : Clone(part.Resolution.Mesh);

        private GameAssetRef MissingRef(string name, long? pathId = null) => new()
        {
            GameBuild = _legacy.AuthoredAgainst?.CatalogVersion ?? "",
            PathId = pathId ?? 0,
            Name = name,
        };

        private void RequireExact(GameAssetRef value, string scope, string label)
        {
            if (string.IsNullOrWhiteSpace(value.GameBuild)
                || string.IsNullOrWhiteSpace(value.LogicalBundle) || value.PathId == 0)
                Report.Add("identity.exact", MigrationDisposition.Unresolved, scope,
                    $"{label} could not be identified in the current game files.");
        }

        private static bool Matches(ProjectTarget target, LegacyResolvedTexture texture)
        {
            if (target.PathId is { } pathId && pathId != 0 && texture.LegacyPathId is { } resolvedPath)
                return pathId == resolvedPath
                    && string.Equals(target.Bundle, texture.LegacyBundle, StringComparison.OrdinalIgnoreCase);
            return string.Equals(target.Bundle, texture.LegacyBundle, StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.ObjectName, texture.LegacyName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFor(ExcludedEdit setting, TargetPart part, string verb) =>
            setting.IsForSubject(part.Subject, part.Outfit)
            && string.Equals(setting.Mesh, part.RendererSlot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(setting.Verb, verb, StringComparison.OrdinalIgnoreCase);

        private static bool IsFor(ChangeKey setting, TargetPart part, string verb) =>
            setting.IsForSubject(part.Subject, part.Outfit)
            && string.Equals(setting.Mesh, part.RendererSlot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(setting.Verb, verb, StringComparison.OrdinalIgnoreCase);

        private static bool IsAssetType(ProjectTarget target, string type) =>
            string.Equals(target.AssetType, type, StringComparison.OrdinalIgnoreCase);

        private static string Scope(TargetPart part) =>
            $"{part.Subject} / {part.Outfit} / {part.RendererSlot}";

        private static ProjectInfo CloneInfo(ProjectInfo source) => new()
        {
            Name = source.Name,
            Version = source.Version,
            Author = source.Author,
            Description = source.Description,
            Character = source.Character,
            Outfit = source.Outfit,
            Preview = source.Preview,
            ToggleKey = source.ToggleKey,
            IncludeRepairData = source.IncludeRepairData,
        };

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

        /// <summary>One game object's identity, for telling two REACHES of the same object apart from two
        /// objects.</summary>
        private static (string, string, long) GameKey(GameAssetRef value) =>
            (value.GameBuild, value.LogicalBundle, value.PathId);

        private static bool Same(GameAssetRef left, GameAssetRef right) =>
            left.PathId == right.PathId
            && string.Equals(left.GameBuild, right.GameBuild, StringComparison.Ordinal)
            && string.Equals(left.LogicalBundle, right.LogicalBundle, StringComparison.Ordinal);

        private static bool Same(Binding left, Binding right) =>
            left.Kind == right.Kind
            && string.Equals(left.ProjectAssetId, right.ProjectAssetId, StringComparison.Ordinal)
            && string.Equals(left.SourceSlot?.SlotId, right.SourceSlot?.SlotId, StringComparison.Ordinal)
            && string.Equals(left.SourceSlot?.EditDefinitionId, right.SourceSlot?.EditDefinitionId,
                StringComparison.Ordinal);

        private static bool FilesEqual(string left, string right)
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);
            if (a.Length != b.Length) return false;
            using var sa = File.OpenRead(left);
            using var sb = File.OpenRead(right);
            Span<byte> ba = stackalloc byte[8192];
            Span<byte> bb = stackalloc byte[8192];
            while (true)
            {
                int na = ReadFull(sa, ba);
                int nb = ReadFull(sb, bb);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!ba[..na].SequenceEqual(bb[..nb])) return false;
            }
        }

        private static int ReadFull(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer[total..]);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        /// <summary>One adapted edit held until every edit sharing its released key can become one group.</summary>
        private sealed record KeyedEdit(string EditId, TargetPart Target, string Key, bool StartsOff,
            string? OffEditId);

        private sealed class PartState(TargetPart route, LegacyResolvedPart? resolution)
        {
            internal TargetPart Route { get; } = route;
            internal LegacyResolvedPart? Resolution { get; } = resolution;
            internal EditDefinition? Edit { get; set; }
            internal EditDefinition? HideEdit { get; set; }
            internal Dictionary<string, TargetSlot> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);
            internal bool Hidden { get; set; }
            internal bool HasGeometry { get; set; }
            internal bool HasTexture { get; set; }
            internal bool HasRamp { get; set; }
        }
    }
}
