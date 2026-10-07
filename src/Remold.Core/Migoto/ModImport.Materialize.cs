using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Migoto;

/// <summary>What an import wrote, and what the game has moved under it since the mod was built.</summary>
public sealed record ImportResult(string ProjectFolder, IReadOnlyList<TargetPart> ChangedTargets);

public static partial class ModImport
{
    /// <summary>Write the project <paramref name="mod"/> describes into <paramref name="destinationFolder"/>,
    /// which the caller has already named and which must not exist yet.
    ///
    /// <para><b>Mid-failure story:</b> everything lands under the destination, and a throw takes the whole
    /// destination with it, so a failed import leaves no half-project for the library to list.</para></summary>
    /// <param name="bundleContentHash">The install's current content hash for a logical bundle. The result
    /// names the parts whose game files have moved since the build.</param>
    /// <param name="resolvePart">The install's current identity for a part. A HIDE is the one change whose
    /// record states no binding — the folder says a part does not draw and nothing more — so its slot's
    /// exact game reference has no source but the install.</param>
    /// <param name="rosterSlots">Every renderer slot the install answers for on a subject, paired with
    /// <paramref name="resolvePart"/>. A value read off a material the record places on no part is found
    /// on the install by walking the mod's subjects with it.</param>
    /// <param name="outfitsOf">The stem of every outfit the roster holds for a character. Where no outfit
    /// the record names carries that material, the walk goes on through the other outfits of the
    /// characters the record names, and no further.</param>
    /// <param name="includeRepairData">What the imported project's own builds ship, which is this app's
    /// current setting rather than whatever the mod in hand was built with: the person importing is the
    /// one who decides what their builds carry.</param>
    public static ImportResult Materialize(ImportableMod mod, string destinationFolder,
        Func<string, string?> bundleContentHash,
        Func<TargetPart, LegacyResolvedPart?> resolvePart,
        Func<string, string, IReadOnlyList<string>> rosterSlots,
        Func<string, IReadOnlyList<string>> outfitsOf,
        bool includeRepairData)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(bundleContentHash);
        ArgumentNullException.ThrowIfNull(resolvePart);
        ArgumentNullException.ThrowIfNull(rosterSlots);
        ArgumentNullException.ThrowIfNull(outfitsOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        string root = Path.GetFullPath(destinationFolder);
        // A failure below removes the destination, so it may only ever be a folder this call made. Handed
        // one that already exists, it refuses rather than putting someone else's folder at that risk.
        if (Directory.Exists(root))
            throw new ArgumentException("the import destination already exists",
                nameof(destinationFolder));
        Directory.CreateDirectory(root);
        try
        {
            new Rebuild(mod, root, resolvePart, rosterSlots, outfitsOf, includeRepairData).Run();
            return new ImportResult(root, ChangedTargets(mod.Payload, bundleContentHash));
        }
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* the throw below is the real report; a leftover folder costs only disk */ }
            throw;
        }
    }

    /// <summary>The parts whose source bundle no longer holds the content the build read, in the record's
    /// own order. Each is the part's IDENTITY rather than its renderer slot alone, so a surface can call it
    /// whatever it calls that part everywhere else. A change that recorded no bundle content says nothing —
    /// an absent identity was already a fact when the mod was built.</summary>
    private static IReadOnlyList<TargetPart> ChangedTargets(RepairData.Payload payload,
        Func<string, string?> bundleContentHash)
    {
        var changed = new List<TargetPart>();
        foreach (var change in payload.Changes)
        {
            if (change.Bundle is not { Length: > 0 } bundle || change.BundleContent is not { Length: > 0 } was)
                continue;
            var part = new TargetPart
            {
                Subject = change.Character,
                Outfit = change.Outfit,
                RendererSlot = change.Mesh,
            };
            if (!string.Equals(bundleContentHash(bundle), was, StringComparison.OrdinalIgnoreCase)
                && !changed.Any(seen => seen.SameAs(part)))
                changed.Add(part);
        }
        return changed;
    }

    /// <summary>One enum value from the spelling the record writes it under. The record and the project
    /// manifest both write enums in snake case, so this is the one inverse both sides share.</summary>
    private static T FromRecordName<T>(string? name, string what) where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
            if (string.Equals(JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!), name,
                    StringComparison.Ordinal))
                return value;
        throw new InvalidDataException($"the repair data names an unknown {what} '{name}'");
    }

    /// <summary>One import's reconstruction. It reads only the record and the files beside it: everything
    /// the build takes from the game is re-read at the next build, so none of it is invented here.</summary>
    private sealed class Rebuild
    {
        private readonly ImportableMod _mod;
        private readonly RepairData.Payload _payload;
        private readonly string _root;
        private readonly AuthoredProject _project = new() { Schema = AuthoredProject.CurrentSchema };

        /// <summary>The intent assets the record carries, by id. The record states an asset's identity and
        /// lineage; which FILE it is comes from the change records, and is settled below.</summary>
        private readonly Dictionary<string, RepairData.IntentAssetRecord> _assets;
        /// <summary>Every asset written EXCEPT geometry, by the id the record names it under.</summary>
        private readonly Dictionary<string, ProjectAsset> _written = new(StringComparer.Ordinal);
        /// <summary>The geometry written, by the edit as well as the record's id: its bytes come from that
        /// edit's own change record, so one id bound by two edits is two meshes. See
        /// <see cref="EnsureAsset"/>.</summary>
        private readonly Dictionary<(string Edit, string Asset), ProjectAsset> _geometry = new();
        /// <summary>Every asset written, by the id the PROJECT carries it under.</summary>
        private readonly Dictionary<string, ProjectAsset> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TargetSlot> _slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EditDefinition> _edits = new(StringComparer.Ordinal);
        private readonly List<ValueFile> _valueFiles = new();
        private readonly List<RepairData.IntentSourceSlotRecord> _retainedSources = new();
        /// <summary>Every binding that takes its value from a slot of the installed game, in the record's
        /// own order. <see cref="ReconstructGameSources"/> reads them once every slot the record describes
        /// is registered.</summary>
        private readonly List<(RepairData.IntentBindingRecord Binding, TargetPart Part)> _gameSources = new();
        /// <summary>Every asset written, beside the record it was written from. <see cref="ResolveLineage"/>
        /// reads the lineage back off these once the whole project is known.</summary>
        private readonly List<(ProjectAsset Asset, RepairData.IntentAssetRecord Record)> _lineage = new();

        private sealed record ValueFile(string Relative, string Semantic, string Value);

        /// <summary>Every shipped row of one edit: the change records (one per key-group position) and the
        /// ramp picks, with the part they all answer for. A group holding neither is a hide that answers a
        /// key-group position and ships nothing of its own; <see cref="Neighbour"/> is then the change whose
        /// position named it, which is what says where the part is in the game files.</summary>
        private sealed class EditRows
        {
            internal required string EditId { get; init; }
            internal required TargetPart Part { get; init; }
            internal List<RepairData.ChangeRecord> Changes { get; } = new();
            internal List<RepairData.StockRampRecord> Ramps { get; } = new();
            internal List<RepairData.StockMaterialRecord> Materials { get; } = new();
            internal RepairData.ChangeRecord? Neighbour { get; set; }

            /// <summary>Hide records a 0.4 build wrote under this edit's name: the part's hide, recorded beside
            /// this edit because this edit shipped no change of its own. They state this edit's intent and
            /// positions, and ship nothing of it.</summary>
            internal List<RepairData.ChangeRecord> Borrowed { get; } = new();

            /// <summary>The intent of an edit whose only change is a ramp pick a 0.4 build recorded under
            /// another edit's name: rebuilt from that record, since no record states it.</summary>
            internal RepairData.IntentRecord? Rebuilt { get; set; }

            /// <summary>The key-group positions of a 0.4 ramp pick that no record states, read off the gate
            /// the mod's own <c>mod.ini</c> binds the ramp under.</summary>
            internal List<RepairData.KeyGroupRecord> Recovered { get; } = new();

            internal RepairData.IntentRecord? Intent => Changes.Count > 0 ? Changes[0].Intent
                : Ramps.FirstOrDefault(pick => string.Equals(pick.Intent?.EditDefinitionId, EditId,
                    StringComparison.Ordinal))?.Intent
                ?? (Materials.Count > 0 ? Materials[0].Intent
                    : Borrowed.Count > 0 ? Borrowed[0].Intent : Rebuilt);

            /// <summary>Whether this edit is the one answer that takes the part off screen.</summary>
            internal bool Hides => Changes.Count > 0
                ? string.Equals(Changes[0].Verb, EditVerbs.Hide, StringComparison.Ordinal)
                : Ramps.Count == 0 && Materials.Count == 0 && Borrowed.Count == 0;

            /// <summary>The change this edit's identity is read off: its own, or the neighbour that named
            /// it.</summary>
            internal RepairData.ChangeRecord? Identity => Changes.Count > 0 ? Changes[0] : Neighbour;
        }

        private readonly Func<TargetPart, LegacyResolvedPart?> _resolvePart;
        private readonly Func<string, string, IReadOnlyList<string>> _rosterSlots;
        private readonly Func<string, IReadOnlyList<string>> _outfitsOf;
        private readonly bool _includeRepairData;

        internal Rebuild(ImportableMod mod, string root,
            Func<TargetPart, LegacyResolvedPart?> resolvePart,
            Func<string, string, IReadOnlyList<string>> rosterSlots,
            Func<string, IReadOnlyList<string>> outfitsOf, bool includeRepairData)
        {
            _mod = mod;
            _payload = mod.Payload;
            _root = root;
            _resolvePart = resolvePart;
            _rosterSlots = rosterSlots;
            _outfitsOf = outfitsOf;
            _includeRepairData = includeRepairData;
            _project.RootDir = root;
            _assets = (_payload.IntentAssets ?? Array.Empty<RepairData.IntentAssetRecord>())
                .ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        }

        internal void Run()
        {
            WriteInfo();
            var groups = Group();
            foreach (var group in groups) BuildEdit(group);
            AdoptRetainedBindings();
            ReconstructGameSources();
            ResolveLineage();
            BuildPlacements(groups);
            BuildWorkspaceIndex(groups);
            WriteValueFiles();
            AuthoredProjectSerializer.Save(_project, _root);
        }

        // ---- identity ------------------------------------------------------------------------------

        private void WriteInfo()
        {
            var info = _project.Info;
            info.Name = _mod.ModName;
            info.Version = string.IsNullOrWhiteSpace(_mod.Version) ? "1.0" : _mod.Version!;
            // the MOD's listed author, never this app's settings: the project records whose work it is
            info.Author = _mod.Author;
            info.Description = _mod.Description;
            info.Character = _mod.Character;
            info.Outfit = _mod.Outfit;
            info.ToggleKey = _payload.ToggleKey;
            info.IncludeRepairData = _includeRepairData;
            // The one thing an imported project says about itself that an authored one cannot: a blank
            // listed author here means the mod listed none, not that this app has yet to fill one in.
            info.Imported = true;
            info.PersistToggleKey = PersistsToggleKey();
            if (_mod.PreviewFile is { } preview) info.Preview = CopyPreview(preview);
            _project.AuthoredAgainst = new AuthoredAgainst { CatalogVersion = _payload.GameCatalog };
            _project.WorkspaceIndex = new AuthoredWorkspaceIndex
            {
                Selection = _payload.Subjects
                    .Select(subject => new SelectionEntry
                    {
                        Character = subject.Character,
                        Outfit = subject.Outfit,
                    }).ToList(),
            };
        }

        /// <summary>Whether the mod's own key keeps its position across launches. The record states it
        /// outright; a record written before it carried that answer states nothing, and the mod's own
        /// <c>mod.ini</c> is then the only place the answer survives — every key is declared there once,
        /// and a key that keeps its position is declared with the runtime's <c>persist</c> word.</summary>
        private bool PersistsToggleKey()
        {
            if (_payload.ToggleKeyPersist is { } stated) return stated;
            if (_payload.ToggleKey is not { Length: > 0 } key) return false;
            string variable;
            try { variable = ModKeys.VariableFor(key); }
            catch (ArgumentException) { return false; }

            string ini = Path.Combine(_mod.Folder, ModIniName);
            if (!File.Exists(ini)) return false;
            foreach (string line in File.ReadLines(ini))
            {
                string rest = line.Trim();
                if (!rest.StartsWith(GlobalWord, StringComparison.Ordinal)) continue;
                rest = rest[GlobalWord.Length..].TrimStart();
                bool persists = rest.StartsWith(PersistWord, StringComparison.Ordinal);
                if (persists) rest = rest[PersistWord.Length..].TrimStart();
                if (rest.Length == 0 || rest[0] != '$') continue;
                int assign = rest.IndexOf('=');
                string named = (assign < 0 ? rest[1..] : rest[1..assign]).Trim();
                if (string.Equals(named, variable, StringComparison.Ordinal)) return persists;
            }
            return false;
        }

        private const string ModIniName = "mod.ini";
        private const string GlobalWord = "global ";
        private const string PersistWord = "persist ";

        private string CopyPreview(string preview)
        {
            string relative = "preview" + Path.GetExtension(preview).ToLowerInvariant();
            Copy(preview, relative);
            return relative;
        }

        // ---- the edits -----------------------------------------------------------------------------

        /// <summary>The shipped rows gathered under the edit that produced them. A part answered by three
        /// key-group positions writes three change records for ONE edit, and a tier writes no record of its
        /// own, so the edit id is the grouping and the row order inside it is the record's.</summary>
        private List<EditRows> Group()
        {
            var groups = new List<EditRows>();
            var byId = new Dictionary<string, EditRows>(StringComparer.Ordinal);

            EditRows For(string? editId, string character, string outfit, string mesh)
            {
                if (string.IsNullOrWhiteSpace(editId))
                    throw new InvalidDataException(
                        $"the change on '{mesh}' names no edit, so it cannot be read back");
                if (byId.TryGetValue(editId!, out var found)) return found;
                var created = new EditRows
                {
                    EditId = editId!,
                    Part = new TargetPart { Subject = character, Outfit = outfit, RendererSlot = mesh },
                };
                byId.Add(editId!, created);
                groups.Add(created);
                return created;
            }

            foreach (var change in _payload.Changes)
            {
                var rows = For(change.Intent?.EditDefinitionId, change.Character, change.Outfit, change.Mesh);
                if (NamesContentEdit(change)) rows.Borrowed.Add(change);
                else rows.Changes.Add(change);
            }
            foreach (var material in _payload.StockMaterials ?? Array.Empty<RepairData.StockMaterialRecord>())
                For(material.Intent?.EditDefinitionId, material.Character, material.Outfit, material.Mesh)
                    .Materials.Add(material);
            // A ramp pick a 0.4 build recorded states neither its positions nor, where its part carries
            // another edit, the edit that made it: the record names the part's first edit. The gate the mod
            // binds the ramp under says which position, and the part's other records say which edit answers
            // that position.
            var rebuilt = new List<(EditRows Rows, RepairData.StockRampRecord Pick)>();
            foreach (var pick in _payload.StockRamps ?? Array.Empty<RepairData.StockRampRecord>())
            {
                if (pick.KeyGroups is not null)
                {
                    For(pick.Intent?.EditDefinitionId, pick.Character, pick.Outfit, pick.Mesh).Ramps.Add(pick);
                    continue;
                }
                var (owner, recovered) = OwnerOf(pick);
                var rows = For(owner, pick.Character, pick.Outfit, pick.Mesh);
                rows.Ramps.Add(pick);
                if (recovered is not null) rows.Recovered.Add(recovered);
                if (!string.Equals(owner, pick.Intent?.EditDefinitionId, StringComparison.Ordinal))
                    rebuilt.Add((rows, pick));
            }
            foreach (var (rows, pick) in rebuilt)
                if (rows.Intent is null) rows.Rebuilt = RampOnlyIntent(pick, rows.EditId);

            // A position that takes a part OFF SCREEN ships nothing of its own — the replacement's own gate
            // carries the suppression — so the hide answering it appears only as a key-group position. It is
            // still an edit the project holds, and without it the position answers nothing.
            foreach (var change in _payload.Changes)
                foreach (var group in change.KeyGroups ?? Array.Empty<RepairData.KeyGroupRecord>())
                    foreach (var state in group.States)
                        if (string.Equals(state.Disposition, "hidden", StringComparison.Ordinal)
                            && state.EditDefinitionId is { Length: > 0 } hiddenId
                            && !byId.ContainsKey(hiddenId))
                            For(hiddenId, change.Character, change.Outfit, change.Mesh)
                                .Neighbour = change;
            return groups;
        }

        private void BuildEdit(EditRows rows)
        {
            bool hide = rows.Hides;
            var edit = new EditDefinition
            {
                Id = rows.EditId,
                Kind = hide ? EditDefinitionKind.Hide : EditDefinitionKind.Content,
                Target = rows.Part,
                Label = LabelOf(rows, hide),
                DisabledMaterialEffects = rows.Intent?.DisabledMaterialEffects is { Count: > 0 } effects
                    ? effects.ToList() : null,
            };
            _edits.Add(edit.Id, edit);
            _project.EditDefinitions.Add(edit);

            // A hide has one answer and the record states it as the verb rather than as a binding: whether a
            // part draws at all is not a value anything reads back off the emitted sections.
            if (hide) { BindHidden(rows, edit); return; }

            // The retained sources' SLOTS come first: they carry whole authored slots, so a binding below
            // that names one of them finds it already registered rather than a missing route. The binding
            // each one carries is filed for the second pass, because it belongs to an edit the record may
            // not have reached yet.
            foreach (var source in rows.Intent?.RetainedMaterialSources
                         ?? Array.Empty<RepairData.IntentSourceSlotRecord>())
            {
                AdoptSourceSlot(source);
                _retainedSources.Add(source);
            }

            var textures = TextureRows(rows);
            foreach (var binding in rows.Intent?.Bindings ?? Array.Empty<RepairData.IntentBindingRecord>())
            {
                var slot = RegisterSlot(binding, rows.Part);
                if (binding.RequestedSourceSlot is { } requested)
                {
                    AdoptSourceSlot(requested);
                    if (requested.EditDefinitionId is null) _gameSources.Add((binding, rows.Part));
                }
                edit.Bindings.Add(new Binding
                {
                    SlotId = slot.Id,
                    Kind = FromRecordName<BindingKind>(binding.RequestedKind, "binding kind"),
                    ProjectAssetId = binding.RequestedProjectAssetId is { } assetId
                        ? EnsureAsset(assetId, rows, slot, binding, textures).Id : null,
                    SourceSlot = binding.RequestedSourceSlot is { } source
                        ? new BindingSourceSlot
                        {
                            SlotId = source.SlotId,
                            EditDefinitionId = source.EditDefinitionId,
                        } : null,
                });
            }
        }

        /// <summary>The one binding a hide holds: this part does not draw. The slot addresses the renderer
        /// the change names, which is the identity a re-resolve against another install joins on.</summary>
        private void BindHidden(EditRows rows, EditDefinition edit)
        {
            // The record states a hide as a verb, not as a binding, so the exact objects behind it come from
            // the install the import is read on — the same resolve an older project's conversion takes.
            var resolved = OnInstall(rows.Part)
                ?? throw new InvalidDataException(PartGoneReason(rows.Part.RendererSlot));
            string slotId = $"slot-hidden-{Segment(rows.EditId)}";
            var slot = new TargetSlot
            {
                Id = slotId,
                Part = rows.Part,
                Input = TargetInputKind.Visibility,
                Domain = TargetSlotDomain.Game,
                Renderer = resolved.Renderer,
                Mesh = resolved.Mesh,
            };
            _slots.Add(slot.Id, slot);
            _project.TargetSlots.Add(slot);
            edit.Bindings.Add(new Binding { SlotId = slot.Id, Kind = BindingKind.Hidden });
        }

        /// <summary>The author's own name for this edit, as the key-group positions record it. A mod with no
        /// key groups carries no name, and the edit takes the one a new edit on that part would be given —
        /// the same rule the Edit page mints a name by, so the numbers run per part from one.</summary>
        private string LabelOf(EditRows rows, bool hide)
        {
            string? recorded = KeyGroupsOf(rows)
                .SelectMany(group => group.States)
                .FirstOrDefault(state => string.Equals(state.EditDefinitionId, rows.EditId,
                    StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(state.Label))?.Label;
            if (!string.IsNullOrWhiteSpace(recorded)) return recorded!;
            return hide ? "Hidden" : AuthoredEditSession.NewEditLabel(_project, rows.Part, null);
        }

        /// <summary>The key groups this edit is placed by. An edit that ships no change of its own takes them
        /// from its stock-material and ramp records, or from what its part's gate in <c>mod.ini</c> says.
        /// Where none of those states them — a 0.4 ramp pick, or an edit a 0.4 hide record was written
        /// under — every record on the part lists the part's positions by the edit answering each, and the
        /// groups naming this edit place it. A hide that ships nothing takes them from the change whose
        /// position named it, which is the only record that mentions it at all.</summary>
        private IEnumerable<RepairData.KeyGroupRecord> KeyGroupsOf(EditRows rows)
        {
            var none = Array.Empty<RepairData.KeyGroupRecord>();
            if (rows.Changes.Count > 0) return rows.Changes.SelectMany(change => change.KeyGroups ?? none);
            if (rows.Materials.Count > 0 || rows.Ramps.Count > 0 || rows.Borrowed.Count > 0)
            {
                var own = rows.Materials.SelectMany(material => material.KeyGroups ?? none)
                    .Concat(rows.Ramps.SelectMany(pick => pick.KeyGroups ?? none))
                    .Concat(rows.Recovered).ToList();
                if (own.Count > 0) return own;
                return PartGroups(rows.Part).Where(group => group.States.Any(state =>
                    string.Equals(state.EditDefinitionId, rows.EditId, StringComparison.Ordinal)));
            }
            return rows.Neighbour?.KeyGroups ?? none;
        }

        // ---- records a 0.4 build wrote --------------------------------------------------------------

        /// <summary>Whether a hide record names a content edit rather than the hide. A 0.4 build wrote the
        /// part's hide under the part's first content edit's name wherever that edit shipped no change of its
        /// own. The record still states that edit's intent and the part's positions; the hide itself is the
        /// edit its positions name as hidden.</summary>
        private static bool NamesContentEdit(RepairData.ChangeRecord change) =>
            string.Equals(change.Verb, EditVerbs.Hide, StringComparison.Ordinal)
            && string.Equals(change.Intent?.Disposition, "edit", StringComparison.Ordinal);

        /// <summary>Every key group a record on <paramref name="part"/> carries.</summary>
        private IEnumerable<RepairData.KeyGroupRecord> PartGroups(TargetPart part)
        {
            var none = Array.Empty<RepairData.KeyGroupRecord>();
            bool On(string character, string outfit, string mesh) =>
                string.Equals(character, part.Subject, StringComparison.OrdinalIgnoreCase)
                && string.Equals(outfit, part.Outfit, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mesh, part.RendererSlot, StringComparison.OrdinalIgnoreCase);
            return _payload.Changes.Where(change => On(change.Character, change.Outfit, change.Mesh))
                .SelectMany(change => change.KeyGroups ?? none)
                .Concat((_payload.StockMaterials ?? Array.Empty<RepairData.StockMaterialRecord>())
                    .Where(material => On(material.Character, material.Outfit, material.Mesh))
                    .SelectMany(material => material.KeyGroups ?? none))
                .Concat((_payload.StockRamps ?? Array.Empty<RepairData.StockRampRecord>())
                    .Where(pick => On(pick.Character, pick.Outfit, pick.Mesh))
                    .SelectMany(pick => pick.KeyGroups ?? none));
        }

        /// <summary>Whether <paramref name="key"/> gates through the ini variable <paramref name="variable"/>.
        /// A key that names no usable key gates through none.</summary>
        private static bool Gates(string? key, string variable)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            try { return string.Equals(ModKeys.VariableFor(key), variable, StringComparison.Ordinal); }
            catch (ArgumentException) { return false; }
        }

        /// <summary>Which edit made a ramp pick a 0.4 build recorded, and the key group placing it where no
        /// record on its part does. The position is the one the mod binds the ramp under; the edit answering
        /// that position on the part is the one that made the pick. A pick bound in no position belongs to
        /// the edit its record names.</summary>
        private (string Owner, RepairData.KeyGroupRecord? Recovered) OwnerOf(RepairData.StockRampRecord pick)
        {
            string named = pick.Intent?.EditDefinitionId is { Length: > 0 } id ? id
                : throw new InvalidDataException(
                    $"the toon ramp on '{pick.Mesh}' names no edit, so it cannot be read back");
            var part = new TargetPart { Subject = pick.Character, Outfit = pick.Outfit, RendererSlot = pick.Mesh };
            var gate = RampGate(pick);
            if (gate.Position is not { } position)
            {
                // Several positions answered by one content flag: 0.4 never raised that flag, so the ramp never
                // showed, and only a record on the part can say which positions the edit answers.
                if (gate.Flagged && !PartGroups(part).Any(group => group.States.Any(state =>
                        string.Equals(state.EditDefinitionId, named, StringComparison.Ordinal))))
                    throw new InvalidDataException($"the toon ramp on '{pick.Mesh}' · '{pick.Material}' is used "
                        + "in several states, and neither the record nor the mod's mod.ini says which");
                return (named, null);
            }
            var onPart = PartGroups(part).FirstOrDefault(group => Gates(group.Key, position.Variable));
            if (onPart is not null)
                return (onPart.States.FirstOrDefault(state => state.State == position.State
                        && string.Equals(state.Disposition, "edit", StringComparison.Ordinal))?.EditDefinitionId
                    ?? named, null);
            // No record on the part says which edit answers which position. Picks bound in different
            // positions are different edits, so each position after the named edit's first gets one of its
            // own: what shows in each position is kept, and the extra edits take new names.
            string owner = named;
            if (_recoveredAt.TryGetValue(named, out var first) && first != position)
            {
                for (int n = 2; ; n++)
                {
                    owner = $"{named}-{n}";
                    if (!_recoveredAt.ContainsKey(owner) && !EditIdsInRecord().Contains(owner)) break;
                }
            }
            _recoveredAt.TryAdd(owner, position);
            return (owner, RecoveredGroup(pick, position.Variable, position.State, owner));
        }

        /// <summary>The position each edit a 0.4 ramp pick was placed by has been given, by edit.</summary>
        private readonly Dictionary<string, KeyPosition> _recoveredAt = new(StringComparer.Ordinal);

        /// <summary>Every edit id the record names anywhere: in an intent or at a key-group position.</summary>
        private HashSet<string> EditIdsInRecord()
        {
            var none = Array.Empty<RepairData.KeyGroupRecord>();
            var intents = _payload.Changes.Select(change => change.Intent)
                .Concat((_payload.StockRamps ?? Array.Empty<RepairData.StockRampRecord>()).Select(pick => pick.Intent))
                .Concat((_payload.StockMaterials ?? Array.Empty<RepairData.StockMaterialRecord>())
                    .Select(material => material.Intent));
            var groups = _payload.Changes.SelectMany(change => change.KeyGroups ?? none)
                .Concat((_payload.StockMaterials ?? Array.Empty<RepairData.StockMaterialRecord>())
                    .SelectMany(material => material.KeyGroups ?? none));
            return intents.Select(intent => intent?.EditDefinitionId)
                .Concat(groups.SelectMany(group => group.States).Select(state => state.EditDefinitionId))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>The key position a 0.4 ramp pick is bound under in the mod's own <c>mod.ini</c>: the ramp's
        /// shipped file is loaded by a resource, and every bind of that resource sits inside the <c>if</c>
        /// tests of its gate. The mod's own key is no position. <see cref="RampGateAnswer.Flagged"/> says the
        /// gate tests a content flag instead of a position, which is how 0.4 gated a pick answering several.
        /// A mod without its <c>mod.ini</c> states no position.</summary>
        private RampGateAnswer RampGate(RepairData.StockRampRecord pick)
        {
            string ini = Path.Combine(_mod.Folder, ModIniName);
            if (!File.Exists(ini)) return new RampGateAnswer(null, false);
            var lines = File.ReadAllLines(ini).Select(line => line.Trim()).ToList();
            string? modVariable = null;
            // a mod key that names no usable key declares no variable, so no gate tests it
            if (_payload.ToggleKey is { Length: > 0 } modKey)
                try { modVariable = ModKeys.VariableFor(modKey); }
                catch (ArgumentException) { }

            var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? section = null;
            foreach (string line in lines)
            {
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                var file = Regex.Match(line, @"^filename\s*=\s*(.+)$");
                if (section is not null && file.Success
                    && string.Equals(file.Groups[1].Value.Trim(), pick.Ramp, StringComparison.OrdinalIgnoreCase))
                    resources.Add(section);
            }

            var positions = new HashSet<(string Variable, int State)>();
            bool flagged = false;
            var open = new List<string>();
            foreach (string line in lines)
            {
                if (line.StartsWith('[')) { open.Clear(); continue; }
                if (line.StartsWith("if ", StringComparison.Ordinal)) { open.Add(line); continue; }
                if (line == "endif") { if (open.Count > 0) open.RemoveAt(open.Count - 1); continue; }
                var bind = Regex.Match(line, @"^ps-t\d+\s*=\s*(\S+)$");
                if (!bind.Success || !resources.Contains(bind.Groups[1].Value)) continue;
                foreach (string test in open)
                {
                    var key = Regex.Match(test, @"^if \$(zz_key_\w+) == (\d+)$");
                    if (key.Success && key.Groups[1].Value != modVariable)
                        positions.Add((key.Groups[1].Value, int.Parse(key.Groups[2].Value, CultureInfo.InvariantCulture)));
                    else if (Regex.IsMatch(test, @"^if \$zz_shw_\w+ == 1$")) flagged = true;
                }
            }
            if (positions.Count > 1)
                throw new InvalidDataException($"the toon ramp on '{pick.Mesh}' · '{pick.Material}' is used "
                    + "in more than one key state, and the record doesn't say which one it belongs to");
            return new RampGateAnswer(positions.Count == 1 ? new KeyPosition(positions.First().Variable,
                positions.First().State) : null, flagged);
        }

        private readonly record struct KeyPosition(string Variable, int State);

        private readonly record struct RampGateAnswer(KeyPosition? Position, bool Flagged);

        /// <summary>The key group placing <paramref name="owner"/> at one position, where no record on the part
        /// carries that group. A record on another part carrying a group on the same key gives its identity,
        /// its position names and its shortcuts; a key only this pick uses is read off the mod's own key
        /// sections: its binding, how many positions it steps through, where it starts and whether it keeps
        /// its position.</summary>
        private RepairData.KeyGroupRecord RecoveredGroup(RepairData.StockRampRecord pick, string variable,
            int position, string owner)
        {
            var none = Array.Empty<RepairData.KeyGroupRecord>();
            var known = _payload.Changes.SelectMany(change => change.KeyGroups ?? none)
                .Concat((_payload.StockMaterials ?? Array.Empty<RepairData.StockMaterialRecord>())
                    .SelectMany(material => material.KeyGroups ?? none))
                .Concat((_payload.StockRamps ?? Array.Empty<RepairData.StockRampRecord>())
                    .SelectMany(other => other.KeyGroups ?? none))
                .FirstOrDefault(group => Gates(group.Key, variable));
            string groupId, key;
            int count, start;
            bool persist;
            if (known is not null)
                (groupId, key, count, start, persist) =
                    (known.GroupId, known.Key!, known.StateCount, known.StartState, known.Persist);
            else
            {
                string unreadable = $"the toon ramp on '{pick.Mesh}' · '{pick.Material}' is switched by a key "
                    + "the mod's mod.ini doesn't declare";
                var lines = File.ReadAllLines(Path.Combine(_mod.Folder, ModIniName))
                    .Select(line => line.Trim()).ToList();
                string? Within(string section, string pattern)
                {
                    int at = lines.IndexOf($"[{section}]");
                    if (at < 0) return null;
                    foreach (string line in lines.Skip(at + 1).TakeWhile(line => !line.StartsWith('[')))
                        if (Regex.Match(line, pattern) is { Success: true } match) return match.Groups[1].Value;
                    return null;
                }
                string binding = Within($"Key_{variable}", @"^key\s*=\s*(.+)$") ?? throw new InvalidDataException(unreadable);
                key = ModKeys.Normalize(binding.StartsWith("no_modifiers ", StringComparison.Ordinal)
                        ? binding["no_modifiers ".Length..] : binding)
                    ?? throw new InvalidDataException(unreadable);
                count = int.Parse(Within($"CommandListKey_{variable}", $@"^if \${Regex.Escape(variable)} == (\d+)$")
                    ?? throw new InvalidDataException(unreadable), CultureInfo.InvariantCulture);
                var declared = lines.Select(line => Regex.Match(line,
                        $@"^global\s+(persist\s+)?\${Regex.Escape(variable)}\s*=\s*(\d+)$"))
                    .FirstOrDefault(match => match.Success);
                start = declared is null ? 0 : int.Parse(declared.Groups[2].Value, CultureInfo.InvariantCulture);
                persist = declared?.Groups[1].Success == true;
                groupId = $"key-{variable}";
            }
            if (position >= count)
                throw new InvalidDataException($"the toon ramp on '{pick.Mesh}' · '{pick.Material}' is used in "
                    + "a state its key doesn't have");
            var states = Enumerable.Range(0, count).Select(index =>
            {
                var named = known?.States.FirstOrDefault(state => state.State == index);
                return index == position
                    ? new RepairData.KeyGroupStateRecord(index, "edit", owner, null, named?.StateLabel,
                        named?.Shortcut)
                    : new RepairData.KeyGroupStateRecord(index, "vanilla", null, null, named?.StateLabel,
                        named?.Shortcut);
            }).ToList();
            return new RepairData.KeyGroupRecord(groupId, key, count, start, position, states, persist);
        }

        /// <summary>The intent of an edit whose only change is a ramp pick a 0.4 build recorded under the part's
        /// first edit. That edit's record states every game slot of the part; the pick's edit asks the original
        /// of each, except the picked material's toon ramp, which takes the shipped ramp.</summary>
        private RepairData.IntentRecord RampOnlyIntent(RepairData.StockRampRecord pick, string owner)
        {
            var basis = pick.Intent!;
            var slot = basis.Bindings.FirstOrDefault(binding =>
                    string.Equals(binding.Input, "ramp", StringComparison.Ordinal)
                    && string.Equals(binding.Target.Domain, "game", StringComparison.Ordinal)
                    && string.Equals(binding.Target.Material?.Name, pick.Material, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"the toon ramp on '{pick.Mesh}' · '{pick.Material}' belongs to "
                    + "an edit the record doesn't describe, and the record has no toon ramp on that material");
            string assetId = $"asset-ramp-{Segment(owner)}";
            for (int n = 2; _assets.ContainsKey(assetId); n++) assetId = $"asset-ramp-{Segment(owner)}-{n}";
            _assets.Add(assetId, new RepairData.IntentAssetRecord(assetId, "ramp",
                Path.GetFileNameWithoutExtension(pick.Ramp)));
            return new RepairData.IntentRecord("edit", owner, basis.Bindings.Select(binding =>
                ReferenceEquals(binding, slot)
                    ? binding with
                    {
                        RequestedKind = "project_asset", RequestedProjectAssetId = assetId,
                        RequestedSourceSlot = null, EffectiveKind = "project_asset",
                        EffectiveProjectAssetId = assetId,
                    }
                    : binding with
                    {
                        RequestedKind = "target_game_value", RequestedProjectAssetId = null,
                        RequestedSourceSlot = null, EffectiveKind = "target_game_value",
                        EffectiveProjectAssetId = null,
                    }).ToList());
        }

        /// <summary>The whole authored slot a source-slot request carries, registered on first sight. A
        /// retained material source and an ordinary binding's request both carry one, and either may be the
        /// only place the record describes that slot.</summary>
        private void AdoptSourceSlot(RepairData.IntentSourceSlotRecord source)
        {
            if (source.AuthoredSlot is not { } slot || _slots.ContainsKey(slot.Id)) return;
            _slots.Add(slot.Id, slot);
            _project.TargetSlots.Add(slot);
        }

        /// <summary>The binding each retained material source carries, given back to the edit that owns it.
        /// A source names an edit ANYWHERE in the record, including one the record states after the source,
        /// so this runs once every edit exists rather than per edit: done in the first pass, a source
        /// naming a later edit would be dropped and that edit would come back without the value it
        /// retains.</summary>
        private void AdoptRetainedBindings()
        {
            foreach (var source in _retainedSources)
            {
                if (source.EditDefinitionId is not { } ownerId
                    || source.AuthoredBinding is not { } binding) continue;
                if (!_edits.TryGetValue(ownerId, out var owner)) continue;
                if (owner.Bindings.Any(candidate => string.Equals(candidate.SlotId, binding.SlotId,
                        StringComparison.Ordinal))) continue;
                owner.Bindings.Add(binding);
            }
        }

        /// <summary>Put back the game slots a binding takes its value from that the record describes
        /// nowhere. A build older than the one that writes a request's whole slot names the slot by id
        /// alone, and where that slot sits on a part the mod does not otherwise change, the id is all the
        /// record says about it.
        ///
        /// <para>The binding's effective game asset IS the material the value was read off, so anywhere that
        /// material sits gives the rest of the route: which part, which renderer and mesh draw it, and which
        /// material position it is at. A slot the record describes on that material is asked first; where
        /// there is none, the install is asked which part carries it, on the mod's subjects and then on the
        /// other outfits of the characters they belong to. The requesting
        /// binding supplies what the source has to match it on — the input, the shading semantic and the
        /// shader property — and the slot addresses the game.</para>
        ///
        /// <para>Where neither knows the material, the mod is refused by the requesting part's name, and the
        /// throw takes the destination with it.</para>
        ///
        /// <para>Run once every described slot is registered, because a request may name a slot the record
        /// describes on an edit it states later.</para></summary>
        private void ReconstructGameSources()
        {
            foreach (var (binding, part) in _gameSources)
            {
                string slotId = binding.RequestedSourceSlot!.SlotId;
                if (_slots.ContainsKey(slotId)) continue;
                var source = DescribedSource(binding.EffectiveGameAsset)
                    ?? InstalledSource(binding.EffectiveGameAsset)
                    ?? throw new InvalidDataException(UndescribedSourcePartReason(part.RendererSlot));
                var input = FromRecordName<TargetInputKind>(binding.Input, "target input");
                var slot = new TargetSlot
                {
                    Id = slotId,
                    Part = new TargetPart
                    {
                        Subject = source.Part.Subject,
                        Outfit = source.Part.Outfit,
                        RendererSlot = source.Part.RendererSlot,
                    },
                    // The material position is half the route: a source slot filed at no position is a
                    // second route onto a part whose edits answer the one the source sits at, and the
                    // project comes back owing a binding nothing could give it.
                    SubmeshIndex = source.SubmeshIndex,
                    MaterialSlotIndex = source.MaterialSlotIndex,
                    Input = input,
                    ShaderProperty = input == TargetInputKind.Texture ? binding.ShaderProperty : null,
                    Domain = TargetSlotDomain.Game,
                    Semantic = input == TargetInputKind.MaterialValue ? binding.Semantic : null,
                    Renderer = Copy(source.Renderer)!,
                    Mesh = Copy(source.Mesh),
                    Material = Copy(source.Material),
                };
                _slots.Add(slot.Id, slot);
                _project.TargetSlots.Add(slot);
            }
        }

        /// <summary>Where a material sits: the part, the objects that draw it, and its position there.</summary>
        private sealed record SourceRoute(TargetPart Part, int? SubmeshIndex, int? MaterialSlotIndex,
            GameAssetRef? Renderer, GameAssetRef? Mesh, GameAssetRef? Material);

        /// <summary>A game slot the record describes on <paramref name="material"/>. See
        /// <see cref="ReconstructGameSources"/>.</summary>
        private SourceRoute? DescribedSource(GameAssetRef? material) => material is null ? null
            : _project.TargetSlots.FirstOrDefault(slot => slot.Domain == TargetSlotDomain.Game
                && SameGameAsset(slot.Material, material)) is { } sibling
                ? new SourceRoute(sibling.Part, sibling.SubmeshIndex, sibling.MaterialSlotIndex,
                    sibling.Renderer, sibling.Mesh, sibling.Material)
                : null;

        /// <summary>The first part, in <see cref="SourceOutfits"/> order and the install's part order, whose
        /// materials include <paramref name="material"/>. Any carrier serves: the value is the material's
        /// own, so every part it sits on reads the same one. The walk stops at the first carrier, because
        /// every outfit it reaches is a subject model the install has to put together.
        ///
        /// <para>The submesh a material position draws is the one at the same index — the rule the Edit
        /// page mints a part's slots by; nothing in the install's answer states a different pairing. A
        /// material with no exact identity is never matched: two unknowns are not one object.</para>
        /// </summary>
        private SourceRoute? InstalledSource(GameAssetRef? material)
        {
            if (material is not { PathId: not 0 } || string.IsNullOrWhiteSpace(material.LogicalBundle))
                return null;
            foreach (var (character, outfit) in SourceOutfits())
                foreach (string rendererSlot in _rosterSlots(character, outfit))
                {
                    var route = new TargetPart
                    {
                        Subject = character, Outfit = outfit, RendererSlot = rendererSlot,
                    };
                    if (OnInstall(route) is not { } resolved) continue;
                    if (resolved.Materials.FirstOrDefault(candidate => SameGameAsset(candidate.Material,
                            material)) is not { } carrier) continue;
                    return new SourceRoute(route, carrier.MaterialSlotIndex, carrier.MaterialSlotIndex,
                        resolved.Renderer, resolved.Mesh, carrier.Material);
                }
            return null;
        }

        /// <summary>The outfits <see cref="InstalledSource"/> looks on, each once: every outfit the record
        /// names, in the record's order, then the roster's other outfits of each character the record
        /// names. A mod may read a value off an outfit of a character it changes without changing that
        /// outfit; the walk goes no wider than those characters, because every outfit it reaches is a
        /// subject model to put together. Lazy, so the roster is only asked once the record's own outfits
        /// have come up empty.</summary>
        private IEnumerable<(string Character, string Outfit)> SourceOutfits()
        {
            var walked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var subject in _payload.Subjects)
                if (walked.Add($"{subject.Character}|{subject.Outfit}"))
                    yield return (subject.Character, subject.Outfit);
            foreach (string character in _payload.Subjects.Select(subject => subject.Character)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string outfit in _outfitsOf(character))
                    if (walked.Add($"{character}|{outfit}"))
                        yield return (character, outfit);
        }

        /// <summary>The install's answer for exactly one part, or null where it has none. A hide and a value
        /// read off a part the record does not describe both take the part's objects from here.</summary>
        private LegacyResolvedPart? OnInstall(TargetPart part) =>
            _resolvePart(new TargetPart
            {
                Subject = part.Subject, Outfit = part.Outfit, RendererSlot = part.RendererSlot,
            }) is { } resolved && resolved.Target.SameAs(part) ? resolved : null;

        private static GameAssetRef? Copy(GameAssetRef? reference) => reference is null ? null
            : new GameAssetRef
            {
                GameBuild = reference.GameBuild,
                LogicalBundle = reference.LogicalBundle,
                PathId = reference.PathId,
                Name = reference.Name,
            };

        /// <summary>One target slot as the record states it. The structural route and the exact game
        /// references are the whole identity; nothing here is derived from a name.</summary>
        private TargetSlot RegisterSlot(RepairData.IntentBindingRecord binding, TargetPart part)
        {
            if (_slots.TryGetValue(binding.SlotId, out var standing)) return standing;
            var input = FromRecordName<TargetInputKind>(binding.Input, "target input");
            var domain = FromRecordName<TargetSlotDomain>(binding.Target.Domain, "target domain");
            var slot = new TargetSlot
            {
                Id = binding.SlotId,
                Part = part,
                Tier = binding.Target.Tier,
                SubmeshIndex = binding.Target.SubmeshIndex,
                MaterialSlotIndex = binding.Target.MaterialSlotIndex,
                Input = input,
                ShaderProperty = input == TargetInputKind.Texture ? binding.ShaderProperty : null,
                Domain = domain,
                // a semantic belongs to a shading value and to nothing else
                Semantic = input == TargetInputKind.MaterialValue ? binding.Semantic : null,
                Renderer = binding.Target.Renderer,
                Mesh = binding.Target.Mesh,
                // an edit's own output has no installed material to address
                Material = domain == TargetSlotDomain.Game ? binding.Target.Material : null,
            };
            _slots.Add(slot.Id, slot);
            _project.TargetSlots.Add(slot);
            return slot;
        }

        // ---- the assets ----------------------------------------------------------------------------

        /// <summary>This edit's per-submesh map record, by submesh. One edit's states all answer with the
        /// same bindings, so the first change that ships rows carries them for the whole group.</summary>
        private static Dictionary<int, RepairData.SubmeshRecord> TextureRows(EditRows rows) =>
            (rows.Changes.FirstOrDefault(change => change.Textures is { Count: > 0 })?.Textures
             ?? Array.Empty<RepairData.SubmeshRecord>())
            .GroupBy(row => row.Submesh)
            .ToDictionary(group => group.Key, group => group.First());

        private ProjectAsset EnsureAsset(string assetId, EditRows rows, TargetSlot slot,
            RepairData.IntentBindingRecord binding, IReadOnlyDictionary<int, RepairData.SubmeshRecord> textures)
        {
            if (!_assets.TryGetValue(assetId, out var record))
                throw new InvalidDataException($"the repair data binds missing asset '{assetId}'");
            var kind = FromRecordName<ProjectAssetKind>(record.Kind, "asset kind");

            // Geometry is the one asset whose BYTES come from the edit's own change record rather than from
            // the record's asset list, so one asset id bound by two edits stands for two different meshes —
            // a part answered one way in one key-group position and another way in the next. Each edit
            // therefore gets its own geometry asset: the first edit to bind the id keeps it, and a later
            // one is minted a fresh id and written from its own shipped streams. Every other kind is one
            // file, reached by whichever edit binds it first.
            if (kind == ProjectAssetKind.Geometry)
            {
                if (_geometry.TryGetValue((rows.EditId, assetId), out var made)) return made;
            }
            else if (_written.TryGetValue(assetId, out var standing)) return standing;

            string id = kind == ProjectAssetKind.Geometry ? MintAssetId(assetId) : assetId;
            var asset = new ProjectAsset
            {
                Id = id,
                Kind = kind,
                Label = string.IsNullOrWhiteSpace(record.Label) ? id : record.Label,
                // lineage is settled in a second pass: which assets this project holds is not known until
                // every edit has been read
                Value = record.Value,
            };
            asset.File = kind switch
            {
                ProjectAssetKind.Geometry => WriteGeometry(rows, id, out var space) is { } geometryFile
                    ? Recorded(asset, space, geometryFile) : throw NoFile(id),
                ProjectAssetKind.Picture => WritePicture(rows, id, slot, binding, textures),
                ProjectAssetKind.Ramp => WriteRamp(rows, id, slot, binding, textures),
                ProjectAssetKind.StructuredValue => WriteValue(asset),
                _ => throw new InvalidDataException($"asset '{id}' has no kind this app reconstructs"),
            };
            if (kind == ProjectAssetKind.Geometry) _geometry.Add((rows.EditId, assetId), asset);
            else _written.Add(assetId, asset);
            _byId.Add(id, asset);
            _lineage.Add((asset, record));
            _project.ProjectAssets.Add(asset);
            return asset;
        }

        /// <summary>An id no asset of this project already carries and no asset of the record names: the
        /// record's own id while it is free, and a numbered form of it after that.</summary>
        private string MintAssetId(string recordId)
        {
            if (!_byId.ContainsKey(recordId)) return recordId;
            for (int next = 2; ; next++)
            {
                string candidate = $"{recordId}-{next}";
                if (!_byId.ContainsKey(candidate) && !_assets.ContainsKey(candidate)) return candidate;
            }
        }

        /// <summary>Give every imported asset the lineage this project can actually state.
        ///
        /// <para>The record carries each asset's whole history — the project asset it was made from, the
        /// one THAT was made from, and so on — while an import materializes only the assets the mod's
        /// edits bind. A lineage step onto an asset this project does not hold names nothing, so the chain
        /// is followed past it: the first step landing on an asset the project DOES hold is kept, and
        /// where the chain leaves the project altogether only a GAME asset at its root survives — which
        /// object of the install the work started from is still a fact. A chain ending at neither leaves
        /// the asset with no lineage, which is what an imported project has: no workspace history.</para>
        ///
        /// <para>Run once every edit exists, because which assets the project holds is not settled before
        /// then.</para></summary>
        private void ResolveLineage()
        {
            foreach (var (asset, record) in _lineage) asset.Source = SourceOf(record);
        }

        /// <inheritdoc cref="ResolveLineage"/>
        private ProjectAssetSource? SourceOf(RepairData.IntentAssetRecord record)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { record.Id };
            var source = record.Source;
            while (source is not null)
            {
                if (source.GameAsset is not null)
                    return new ProjectAssetSource { GameAsset = source.GameAsset };
                if (source.ProjectAssetId is not { } parentId) return null;
                // a lineage that loops, or that leaves the record, names no root at all
                if (!seen.Add(parentId) || !_assets.TryGetValue(parentId, out var parent)) return null;
                if (HeldAs(parentId) is { } held)
                    return new ProjectAssetSource { ProjectAssetId = held };
                source = parent.Source;
            }
            return null;
        }

        /// <summary>The id this project holds the record's asset under, or null where it holds none — or
        /// holds more than one, which is what a geometry asset two edits bind comes back as: which of the
        /// two a lineage meant is not something the record says.</summary>
        private string? HeldAs(string recordId)
        {
            if (_written.TryGetValue(recordId, out var written)) return written.Id;
            var copies = _geometry
                .Where(pair => string.Equals(pair.Key.Asset, recordId, StringComparison.Ordinal))
                .Select(pair => pair.Value.Id).Distinct(StringComparer.Ordinal).Take(2).ToList();
            return copies.Count == 1 ? copies[0] : null;
        }

        private static string Recorded(ProjectAsset asset, GeometrySpace space, string file)
        {
            asset.BakedRest = space.BakedRest?.ToList();
            asset.HiddenCentred = space.HiddenCentred;
            return file;
        }

        /// <summary>The space a rebuilt geometry file sits in, as the record put it back: its rest, and
        /// whether it was authored with hidden parts shown centred. It records no centre: the file is written
        /// where the part was modelled (see <see cref="WriteGeometry"/>).</summary>
        private readonly record struct GeometrySpace(IReadOnlyList<float>? BakedRest, bool? HiddenCentred);

        private static InvalidDataException NoFile(string assetId) =>
            new($"the mod has no file for '{assetId}'");

        /// <summary>The name the picture is stored under. A retextured map ships under a name the build
        /// derives from the picture's OWN file name, so keeping that name is what lets a rebuild bind the
        /// same file rather than an equivalent one under another name. A shipped file named any other way
        /// falls back to the asset's own id.</summary>
        private string AuthoredName(string shipped, string assetId)
        {
            const string prefix = "rtx_";
            string stem = Path.GetFileNameWithoutExtension(shipped);
            if (!stem.StartsWith(prefix, StringComparison.Ordinal)) return assetId;
            stem = stem[prefix.Length..];
            // the colour-family suffix a second encode of one picture takes, then the map-kind letter
            foreach (string family in new[] { "_srgb", "_lin" })
                if (stem.EndsWith(family, StringComparison.Ordinal))
                    stem = stem[..^family.Length];
            int kind = stem.LastIndexOf('_');
            if (kind > 0) stem = stem[..kind];
            return stem.Length > 0 && !_takenNames.Contains(stem) && _takenNames.Add(stem)
                ? stem : assetId;
        }

        private readonly HashSet<string> _takenNames = new(StringComparer.OrdinalIgnoreCase);

        private static string SlotFile(EditRows rows, string slotId, string name, string extension) =>
            $"assets/edits/{Segment(rows.EditId)}/slots/{Segment(slotId)}/{name}{extension}";

        private static string Segment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
                || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || value.Contains('/') || value.Contains('\\'))
                throw new InvalidDataException($"'{value}' is not a safe asset-storage id");
            return value;
        }

        // ---- geometry ------------------------------------------------------------------------------

        /// <summary>The replacement's own geometry, written back as the rigged glb an edit binds. The mod
        /// ships one compiled geometry per edit, so every geometry binding of that edit reads it.</summary>
        private string? WriteGeometry(EditRows rows, string assetId, out GeometrySpace space)
        {
            space = default;
            var change = rows.Changes.FirstOrDefault(candidate => candidate.Geometry is not null);
            if (change?.Geometry is not { } geometry) return null;

            var decoded = DecodeShipped(geometry, rows.Part.RendererSlot);
            bool sceneRest = string.Equals(geometry.Union?.Space, "scene_rest", StringComparison.Ordinal);
            // A rest the reader refuses is refused in Inspect. Re-asked here rather than assumed: the one
            // way a refused rest could reach this is a record read by some other route, and a rest quietly
            // dropped ships geometry in a space nothing states.
            var recorded = RestBake.FromList(change.BakedRest, out bool restRefused);
            if (restRefused)
                throw new InvalidDataException(
                    $"the rest pose recorded for '{rows.Part.RendererSlot}' can't be read");
            // The shipped positions are what the BUILD compiled, and the build states the donor in the
            // union's space first: it takes a recorded rest back OFF a file stated in scene-rest space, and
            // stands a file up that states none. Writing the file the build would consume again means
            // putting the recorded rest back ON, and recording it so the next build takes it off the same
            // way. A scene-rest union with nothing recorded is the one shape that cannot be stated: the
            // stand it applied is the install's, not the mod's, so it is refused in Inspect.
            //
            // The rest goes to the writer as its uprighting rather than onto the mesh here: the writer
            // moves geometry and joints together, and a mesh stood up on its own would sit in a rig still
            // in bind space — which is what a modder would open in Blender.
            var uprighting = sceneRest ? null : recorded;
            // A part the game starts hidden opened centred, and the build took that centre off before
            // anything else, so what shipped sits where the part was modelled. The file is written there and
            // records no centre: it then builds to the same bytes, and opening it in Blender centres it the
            // way an edit returned before hidden parts opened centred is centred. The record's centre is
            // not read. The relation the replacement was authored under is kept as recorded.
            space = new GeometrySpace(change.BakedRest, change.HiddenCentred == true ? true : null);

            string relative = $"assets/edits/{Segment(rows.EditId)}/geometry/{assetId}.glb";
            string full = Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (decoded.Skin is { } skin)
                MeshGltf.ExportRiggedGlb(decoded.Mesh, skin, _ => null, full, uprighting: uprighting);
            else MeshGltf.ExportGlb(decoded.Mesh, full, uprighting: uprighting);
            return relative;
        }

        private sealed record DecodedGeometry(UnityMesh Mesh, MeshSkin? Skin);

        /// <summary>The shipped buffers read back into a mesh. The record's channel table says where every
        /// channel sits, the stream NUMBER joins a channel to the buffer holding it, and the union plus the
        /// coverage-group slots say which bone each blend index names.</summary>
        private DecodedGeometry DecodeShipped(RepairData.GeometryRecord geometry, string meshName)
        {
            var shipped = geometry.Streams.ToDictionary(stream => stream.Stream, stream => stream.File);
            // A live channel whose stream ships in no buffer is one whose BYTES are not in the mod. It is
            // dropped rather than filled: the build re-derives such a channel from the target's own layout,
            // and a zero-filled one would ship as authored data.
            var channels = geometry.Channels
                .Select(channel => new UnityMesh.ChannelDef(channel.Stream, channel.Offset, channel.Format,
                    shipped.ContainsKey(channel.Stream) ? channel.Dimension : 0))
                .ToList();

            // Unity lays the streams out end to end, padding every stream but the last up to 16 bytes. The
            // codec states that layout itself, so the blob is assembled from the codec's own answer rather
            // than from a second copy of the rule.
            Dictionary<int, int> strides, starts;
            try { (strides, starts) = UnityMesh.StreamInfo(channels, geometry.Verts); }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    "the record names a vertex format this app can't read", ex);
            }

            var order = strides.Keys.OrderBy(stream => stream).ToList();
            var blob = new byte[order.Count == 0 ? 0
                : starts[order[^1]] + geometry.Verts * strides[order[^1]]];
            foreach (int stream in order)
            {
                int want = geometry.Verts * strides[stream];
                var bytes = File.ReadAllBytes(Shipped(shipped[stream]));
                if (bytes.Length < want)
                    throw new InvalidDataException(
                        $"'{shipped[stream]}' holds {bytes.Length} bytes and the record needs {want}");
                bytes.AsSpan(0, want).CopyTo(blob.AsSpan(starts[stream], want));
            }

            var mesh = UnityMesh.DecodeRaw(meshName, geometry.Verts, channels, blob,
                string.Equals(geometry.IndexFormat, "R32_UINT", StringComparison.Ordinal) ? 1 : 0,
                File.ReadAllBytes(Shipped(geometry.IndexFile)),
                geometry.Submeshes
                    .Select(span => new UnityMesh.SubMeshDef(span.FirstByte, span.IndexCount, span.BaseVertex))
                    .ToList());

            return new DecodedGeometry(mesh, BuildSkin(mesh, geometry, meshName));
        }

        /// <summary>The bone table the shipped blend indices address. Slots below the union count name a
        /// union bone; the rest are coverage-group palette slots the emission reserved, and only the record
        /// says where. A slot named by neither, under weight, is refused by mesh rather than read as bone
        /// 0.</summary>
        private static MeshSkin? BuildSkin(UnityMesh mesh, RepairData.GeometryRecord geometry, string meshName)
        {
            if (geometry.Union is not { } union) return null;
            if (!mesh.Channels.TryGetValue("BlendIndices", out var indices)
                || !mesh.Channels.TryGetValue("BlendWeight", out var weights)) return null;

            var unionBones = union.Bones.Select(ParseBone).ToList();
            var poses = ReadBindPoses(union, unionBones.Count);
            var bySlot = new Dictionary<uint, uint>();
            for (int i = 0; i < unionBones.Count; i++) bySlot[(uint)i] = unionBones[i];
            foreach (var group in geometry.GroupSlots ?? Array.Empty<RepairData.GroupSlot>())
                bySlot[group.Slot] = ParseBone(group.Bone);

            // One entry per bone the skin names, weighted or not, in first-seen order. The compile maps an
            // authored skin onto its own table BY HASH, so a distinct list reproduces the shipped indices
            // whether a bone came from the union or from a coverage group.
            var hashes = new List<uint>();
            var bindPoses = new List<Matrix4x4>();
            var rowOf = new Dictionary<uint, int>();
            int indexDim = mesh.Dims.GetValueOrDefault("BlendIndices", 4);
            int weightDim = mesh.Dims.GetValueOrDefault("BlendWeight", 4);
            var remapped = new float[mesh.VertexCount * 4];
            var remappedWeights = new float[mesh.VertexCount * 4];
            for (int v = 0; v < mesh.VertexCount; v++)
                for (int k = 0; k < 4; k++)
                {
                    float weight = k < weightDim ? weights[v * weightDim + k] : 0f;
                    remappedWeights[v * 4 + k] = weight;
                    if (k >= indexDim) continue;
                    uint slot = (uint)indices[v * indexDim + k];
                    // An influence carrying no weight moves nothing, but the index under it is still the
                    // authoring tool's own byte and the compile writes back whatever the slot resolves to.
                    // Dropping it would make the rebuilt stream differ from the one the mod ships. Only a
                    // WEIGHTED influence names a bone the mod has to account for, so an unnamed slot under
                    // no weight stays on row 0 rather than taking the mesh down.
                    if (!bySlot.TryGetValue(slot, out uint bone))
                    {
                        if (weight <= 0f) continue;
                        throw new InvalidDataException(
                            $"the new mesh for '{meshName}' uses a bone its repair data does not list");
                    }
                    if (!rowOf.TryGetValue(bone, out int row))
                    {
                        row = hashes.Count;
                        rowOf.Add(bone, row);
                        hashes.Add(bone);
                        int source = unionBones.IndexOf(bone);
                        // A coverage-group bone is tabled at the identity, which is what the compile gives
                        // it: the authored skin maps onto that table by hash and the streams carry indices,
                        // so nothing downstream reads a pose there.
                        bindPoses.Add(source >= 0 ? poses[source] : Matrix4x4.Identity);
                    }
                    remapped[v * 4 + k] = row;
                }

            mesh.Channels["BlendIndices"] = remapped;
            mesh.Dims["BlendIndices"] = 4;
            mesh.Channels["BlendWeight"] = remappedWeights;
            mesh.Dims["BlendWeight"] = 4;
            return new MeshSkin { BoneHashes = hashes, BindPoses = bindPoses };
        }

        private static uint ParseBone(string bone) =>
            uint.TryParse(bone, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
                ? value
                : throw new InvalidDataException($"'{bone}' is not a bone the repair data can name");

        private static List<Matrix4x4> ReadBindPoses(RepairData.UnionRecord union, int bones)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(union.BindPoses); }
            catch (FormatException) { throw new InvalidDataException("the recorded bind poses are not base64"); }
            if (raw.Length != bones * 16 * sizeof(float))
                throw new InvalidDataException($"the record states {bones} bones and "
                    + $"{raw.Length / (16 * sizeof(float))} bind poses");
            var poses = new List<Matrix4x4>(bones);
            for (int b = 0; b < bones; b++)
            {
                var m = new float[16];
                Buffer.BlockCopy(raw, b * 16 * sizeof(float), m, 0, 16 * sizeof(float));
                poses.Add(new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]));
            }
            return poses;
        }

        // ---- pictures, ramps and values --------------------------------------------------------------

        private string WritePicture(EditRows rows, string assetId, TargetSlot slot,
            RepairData.IntentBindingRecord binding,
            IReadOnlyDictionary<int, RepairData.SubmeshRecord> textures)
        {
            string shipped = ShippedMap(rows, binding, textures) ?? throw NoFile(assetId);
            string relative = SlotFile(rows, slot.Id, AuthoredName(shipped, assetId), ".png");
            string full = Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            WriteTopDownPng(Shipped(shipped), full);
            return relative;
        }

        private string WriteRamp(EditRows rows, string assetId, TargetSlot slot,
            RepairData.IntentBindingRecord binding,
            IReadOnlyDictionary<int, RepairData.SubmeshRecord> textures)
        {
            string shipped = ShippedMap(rows, binding, textures)
                ?? rows.Ramps.FirstOrDefault()?.Ramp
                ?? throw NoFile(assetId);
            string relative = SlotFile(rows, slot.Id, assetId, ".dds");
            // A ramp's values ARE the shading curve, so the file travels byte for byte.
            Copy(shipped, relative);
            return relative;
        }

        /// <summary>The shipped <c>.dds</c> one picture binding became, joined on the submesh the binding
        /// addresses and the map slot its input names. The encode collapses equal images onto one file, so
        /// this mapping is not derivable from the file's name.</summary>
        private static string? ShippedMap(EditRows rows, RepairData.IntentBindingRecord binding,
            IReadOnlyDictionary<int, RepairData.SubmeshRecord> textures)
        {
            if (binding.Target.SubmeshIndex is not { } submesh
                || !textures.TryGetValue(submesh, out var row)) return null;
            return binding.Input switch
            {
                "base_color" => row.Albedo?.File,
                "normal" => row.Normal?.File,
                "rmo" => row.Rmo?.File,
                "blend" => row.Blend?.File,
                "ramp" => row.Ramp?.File ?? rows.Ramps.FirstOrDefault()?.Ramp,
                "texture" => row.Textures?.FirstOrDefault(texture =>
                    string.Equals(texture.ShaderProperty, binding.ShaderProperty,
                        StringComparison.Ordinal))?.Slot.File,
                _ => null,
            };
        }

        /// <summary>A shipped map back as the top-down picture the workspace holds. The build writes the
        /// rows bottom-up, because the game samples with Unity Vs, so the decode flips them back.</summary>
        private static void WriteTopDownPng(string dds, string png)
        {
            var image = DdsReader.Read(dds, DdsAccepts.AnythingTheBuildWrites);
            var rgba = TextureCodec.DecodeToRgba(image.Levels[0], image.Width, image.Height,
                DecodeFormat(image.DxgiFormat, Path.GetFileName(dds)));
            using var picture = Image.LoadPixelData<Rgba32>(rgba, image.Width, image.Height);
            picture.Mutate(x => x.Flip(FlipMode.Vertical));
            picture.SaveAsPng(png);
        }

        /// <summary>The texture format a shipped map's DXGI tag names, for the decoder. A tag this app does
        /// not write is refused rather than decoded as a guess.</summary>
        private static ATTextureFormat DecodeFormat(uint dxgi, string file) => dxgi switch
        {
            DdsWriter.BC7_UNORM or DdsWriter.BC7_UNORM_SRGB => ATTextureFormat.BC7,
            DdsWriter.R8G8B8A8_UNORM or DdsWriter.R8G8B8A8_UNORM_SRGB => ATTextureFormat.RGBA32,
            _ => throw new InvalidDataException(
                $"'{file}' is tagged with a texture format this app cannot read"),
        };

        private string WriteValue(ProjectAsset asset)
        {
            if (asset.Value is not { } value || string.IsNullOrWhiteSpace(value.Semantic))
                throw new InvalidDataException($"'{asset.Id}' has no shading value");
            string relative = $"values/imported/{asset.Id}.json";
            _valueFiles.Add(new ValueFile(relative, value.Semantic, value.Value));
            return relative;
        }

        private void WriteValueFiles()
        {
            foreach (var file in _valueFiles)
            {
                string full = Resolve(file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, JsonSerializer.Serialize(new
                {
                    semantic = file.Semantic,
                    value = file.Value,
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
        }

        // ---- placement -------------------------------------------------------------------------------

        /// <summary>Where each edit is placed: the Always list, or the key-group positions the record filed
        /// it under. An edit the record shows in EVERY position of every group it touches was placed once,
        /// in Always — a group cannot answer a part the same way in all its positions and still be
        /// switching anything.</summary>
        private void BuildPlacements(IReadOnlyList<EditRows> groups)
        {
            var states = new Dictionary<(string Group, int State), List<string>>();
            var records = new Dictionary<string, RepairData.KeyGroupRecord>(StringComparer.Ordinal);

            foreach (var rows in groups)
            {
                var touched = KeyGroupsOf(rows)
                    .GroupBy(group => group.GroupId, StringComparer.Ordinal)
                    .Select(group => group.First()).ToList();
                foreach (var group in touched) records.TryAdd(group.GroupId, group);

                bool always = touched.Count == 0 || touched.All(group => group.States
                    .All(state => string.Equals(state.EditDefinitionId, rows.EditId, StringComparison.Ordinal)));
                if (always)
                {
                    if (!_project.Always.Contains(rows.EditId, StringComparer.Ordinal))
                        _project.Always.Add(rows.EditId);
                    continue;
                }
                foreach (var group in touched)
                    foreach (var state in group.States)
                        if (string.Equals(state.EditDefinitionId, rows.EditId, StringComparison.Ordinal))
                        {
                            var list = states.TryGetValue((group.GroupId, state.State), out var found)
                                ? found : states[(group.GroupId, state.State)] = new List<string>();
                            if (!list.Contains(rows.EditId, StringComparer.Ordinal)) list.Add(rows.EditId);
                        }
            }

            foreach (var (groupId, record) in records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                _project.KeyGroups.Add(new KeyGroup
                {
                    Id = groupId,
                    Key = record.Key,
                    Persist = record.Persist,
                    States = Enumerable.Range(0, record.StateCount).Select(index => new KeyGroupState
                    {
                        Id = $"state-{index + 1:D4}",
                        // the POSITION's own name, which the author gives apart from any edit's name
                        Label = record.States.FirstOrDefault(state => state.State == index)?.StateLabel,
                        ActiveEditIds = states.GetValueOrDefault((groupId, index)) ?? new List<string>(),
                        Shortcut = ModKeys.Normalize(
                            record.States.FirstOrDefault(state => state.State == index)?.Shortcut),
                    }).ToList(),
                });
        }

        /// <summary>The workspace inventory the Edit surface projects its one-edit-per-part controls from.
        /// It carries cache facts only: the replacement's own file, the vertex count of the mesh it stands
        /// in for, and the space that file is stated in.</summary>
        private void BuildWorkspaceIndex(IReadOnlyList<EditRows> groups)
        {
            var index = _project.WorkspaceIndex!;
            int next = 1;
            foreach (var rows in groups)
            {
                var change = rows.Changes.FirstOrDefault(candidate => candidate.Geometry is not null);
                if (change is null) continue;
                // This edit's OWN geometry binding, never the part's first geometry slot: a part answered
                // by two edits has two of them, and reading the slot off the part alone files one edit's
                // mesh under the other edit's route. An edit that binds no geometry has nothing to index.
                var (slot, asset) = GeometryOf(rows.EditId);
                if (slot?.Mesh is null || asset is null) continue;
                index.Records.Add(new AuthoredWorkspaceRecord
                {
                    Id = $"workspace-{next++:D4}",
                    Kind = ProjectAssetKind.Geometry,
                    Part = rows.Part,
                    GameAsset = slot.Mesh,
                    SlotId = slot.Id,
                    ProjectFile = asset.File,
                    OriginalVertices = change.OriginalVerts,
                    BakedRest = change.BakedRest?.ToList(),
                });
            }
        }

        /// <inheritdoc cref="BuildWorkspaceIndex"/>
        private (TargetSlot? Slot, ProjectAsset? Asset) GeometryOf(string editId)
        {
            foreach (var binding in _edits[editId].Bindings)
                if (binding.ProjectAssetId is { } assetId && _byId.TryGetValue(assetId, out var asset)
                    && asset.Kind == ProjectAssetKind.Geometry
                    && _slots.TryGetValue(binding.SlotId, out var slot)
                    && slot.Input == TargetInputKind.Geometry)
                    return (slot, asset);
            return (null, null);
        }

        // ---- files -----------------------------------------------------------------------------------

        private string Resolve(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(_root, relative));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("an imported file escapes the project folder");
            return full;
        }

        private void Copy(string shippedName, string relative)
        {
            string full = Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(Shipped(shippedName), full, overwrite: true);
        }

        /// <summary>One file the record names, as a path inside the MOD folder. Every read of a shipped file
        /// goes through here for the same reason every write goes through <see cref="Resolve"/>: the record
        /// travelled with the mod, so a name in it is as untrusted as the mod is.</summary>
        private string Shipped(string file) => ShippedPath(_mod.Folder, file);
    }
}
