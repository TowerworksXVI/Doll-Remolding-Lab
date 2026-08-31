using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Remold.Core.Mesh;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>
/// The <c>repair.json</c> a build leaves at the mod root: everything a later pass needs to read the mod
/// back into a project without the author's workspace.
///
/// <para><b>What is here and what is not.</b> Everything the build reads from the GAME — which bundle an
/// address resolves to, the bytes behind it, the sharing measurement, wardrobe schemes, timeline overrides
/// — is re-read from whatever install the mod is read back on, so none of it is recorded. What is recorded
/// is the modder's INTENT, the IDENTITY of what that intent was pointed at, and the one key that makes the
/// shipped geometry portable: the bone order its blend indices address.</para>
///
/// <para><b>The shipped streams ARE the donor.</b> A Replace's <c>combined_*</c>/<c>rigid_*</c> buffers
/// hold the authored positions, UVs, skin and topology; they are unreadable against a different install
/// only because their blend indices address a bone order the mod otherwise does not describe, and their
/// channels sit at offsets only the target's layout table gives. Both travel here, so no donor glb and no
/// authored PNG has to ship.</para>
///
/// <para><b>Deterministic.</b> No timestamps, no paths off the building machine, and every list in the
/// build's own settled order — two builds of one project write identical bytes.</para>
/// </summary>
public static class RepairData
{
    public const string FileName = "repair.json";

    /// <summary>The payload's own schema, versioned independently of <c>gf2mod.json</c> (frozen at 1) so
    /// this can move without touching the sidecar a mod manager parses on every card.</summary>
    public const int LegacySchema = 1;
    public const int Schema = 2;

    // ---- the payload ------------------------------------------------------------------------------

    /// <summary>One subject the mod touches.</summary>
    public sealed record SubjectRef(
        [property: JsonPropertyName("character")] string Character,
        [property: JsonPropertyName("outfit")] string Outfit);

    /// <summary>Stable schema-2 asset identity needed to reconstruct authored intent. Runtime files are
    /// described by the change geometry/texture records; this metadata deliberately does not pretend the
    /// author's original workspace-relative file shipped unchanged.</summary>
    public sealed record IntentAssetRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("source")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectAssetSource? Source = null,
        [property: JsonPropertyName("value")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectAssetValue? Value = null);

    public sealed record IntentSourceSlotRecord(
        [property: JsonPropertyName("slot_id")] string SlotId,
        [property: JsonPropertyName("edit_definition_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditDefinitionId = null);

    public sealed record IntentProofRecord(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("detail")] string Detail);

    public sealed record IntentTargetSlotRecord(
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("tier")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tier,
        [property: JsonPropertyName("submesh_index")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SubmeshIndex,
        [property: JsonPropertyName("material_slot_index")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaterialSlotIndex,
        [property: JsonPropertyName("renderer")] GameAssetRef Renderer,
        [property: JsonPropertyName("mesh")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameAssetRef? Mesh,
        [property: JsonPropertyName("material")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameAssetRef? Material);

    /// <summary>One normalized binding as consumed by the successful Build plan. Requested intent and
    /// effective source remain separate so Import can recreate the binding rather than reverse-engineer
    /// it from an emitted resource name.</summary>
    public sealed record IntentBindingRecord(
        [property: JsonPropertyName("slot_id")] string SlotId,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("semantic")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Semantic,
        [property: JsonPropertyName("target")] IntentTargetSlotRecord Target,
        [property: JsonPropertyName("requested_kind")] string RequestedKind,
        [property: JsonPropertyName("requested_project_asset_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedProjectAssetId,
        [property: JsonPropertyName("requested_source_slot")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IntentSourceSlotRecord? RequestedSourceSlot,
        [property: JsonPropertyName("effective_kind")] string EffectiveKind,
        [property: JsonPropertyName("effective_project_asset_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveProjectAssetId,
        [property: JsonPropertyName("effective_game_asset")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameAssetRef? EffectiveGameAsset,
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("targeting_proof")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IntentProofRecord? TargetingProof,
        [property: JsonPropertyName("emission_ids")] IReadOnlyList<string> EmissionIds,
        [property: JsonPropertyName("shader_property")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShaderProperty = null);

    /// <summary>The schema-2 edit activation identity that produced a shipped change.</summary>
    public sealed record IntentRecord(
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("edit_definition_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditDefinitionId,
        [property: JsonPropertyName("bindings")] IReadOnlyList<IntentBindingRecord> Bindings);

    /// <summary>What ONE position of a key group answers for the part this record belongs to.
    /// <paramref name="Label"/> is the edit's own name as the author gave it, carried so a read can show
    /// the position without having to invent one.</summary>
    public sealed record KeyGroupStateRecord(
        [property: JsonPropertyName("state")] int State,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("edit_definition_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditDefinitionId = null,
        [property: JsonPropertyName("label")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null);

    /// <summary>One key group containing a placement for this change's part.</summary>
    public sealed record KeyGroupRecord(
        [property: JsonPropertyName("group_id")] string GroupId,
        [property: JsonPropertyName("key")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key,
        [property: JsonPropertyName("state_count")] int StateCount,
        [property: JsonPropertyName("start_state")] int StartState,
        [property: JsonPropertyName("state_index")] int StateIndex,
        [property: JsonPropertyName("states")] IReadOnlyList<KeyGroupStateRecord> States);

    /// <summary>A tier-2 key binding, whole: an off state and a start state travel with the key.</summary>
    public sealed record KeyBinding(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("hide_when_off")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HideWhenOff,
        [property: JsonPropertyName("starts_off")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool StartsOff);

    /// <summary>The origin of a slot NO ask filled: the build carried the game map the donor geometry was
    /// shaded by across with it. Not one of <see cref="SlotOrigin"/>'s values, because the modder asked for
    /// nothing — a read that treats this as a chosen file would offer to preserve a decision nobody made.
    /// Today only the toon ramp is carried this way.</summary>
    public const string CarriedFromDonor = "DonorVanilla";

    /// <summary>What one map slot of one submesh asked for. <paramref name="Origin"/> is the whole
    /// per-slot contract — the emitted binds cannot tell blank-this from don't-touch-this — and
    /// <paramref name="File"/> names the shipped <c>.dds</c> the ask ships as, which is not derivable from
    /// the slot: one image authored on several submeshes ships once, under the first claimant's name.</summary>
    public sealed record SlotRecord(
        [property: JsonPropertyName("origin")] string Origin,
        [property: JsonPropertyName("file")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
        [property: JsonPropertyName("stock")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StockTextureRef? Stock = null);

    /// <summary>The game texture a retexture slot overrides, by the identity the game carries it under.
    /// <paramref name="Users"/> is the recorded object name of every mesh binding it, which is what a
    /// reconstructed texture target is shown under. <paramref name="PathId"/> is WHICH object of that
    /// bundle, written where the source is known that exactly — a carried toon ramp, whose asset name a
    /// ramp library repeats across every ramp it holds. Null means the name selects, which is how a
    /// retextured picture map is reached and what a record written before ramp pathIds carries.</summary>
    public sealed record StockTextureRef(
        [property: JsonPropertyName("bundle")] string Bundle,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("users")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Users = null,
        [property: JsonPropertyName("path_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PathId = null);

    /// <summary>One submesh's map slots. A submesh with no record inherits every slot, and so does a slot
    /// with no record of its own — which is what an older mod, or one that ships none, writes for
    /// <paramref name="Ramp"/> and <paramref name="Blend"/>.</summary>
    public sealed record SubmeshRecord(
        [property: JsonPropertyName("submesh")] int Submesh,
        [property: JsonPropertyName("albedo")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SlotRecord? Albedo = null,
        [property: JsonPropertyName("normal")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SlotRecord? Normal = null,
        [property: JsonPropertyName("rmo")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SlotRecord? Rmo = null,
        [property: JsonPropertyName("ramp")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SlotRecord? Ramp = null,
        [property: JsonPropertyName("blend")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SlotRecord? Blend = null,
        [property: JsonPropertyName("textures")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<PropertySlotRecord>? Textures = null);

    /// <summary>One ordinary texture binding in repair data. The exact shader property is identity; the
    /// nested slot retains the same origin/file/stock contract as the fixed legacy fields.</summary>
    public sealed record PropertySlotRecord(
        [property: JsonPropertyName("shader_property")] string ShaderProperty,
        [property: JsonPropertyName("slot")] SlotRecord Slot);

    /// <summary>One appended coverage-group palette slot: the slot the shipped skin stream's blend indices
    /// use, and the bone it stands for. The union covers slots <c>0..bones-1</c>; a group bone's index is
    /// whatever the emission reserved, past the union and past the witness slots, so it is recorded as the
    /// pair rather than as an offset a reader would have to reconstruct.</summary>
    public sealed record GroupSlot(
        [property: JsonPropertyName("slot")] uint Slot,
        [property: JsonPropertyName("bone")] string Bone);

    /// <summary>The bone order the emitted blend indices address, with the bind pose each bone was stated
    /// under. <paramref name="BindPoses"/> is base64 little-endian float32, 16 per bone in
    /// <paramref name="Bones"/> order, row-major as the game stores them — number text would be several
    /// times the size for a few hundred bones. <paramref name="Space"/> names which space those poses are
    /// in: <c>scene_rest</c> or the anchor part's own.</summary>
    public sealed record UnionRecord(
        [property: JsonPropertyName("bones")] IReadOnlyList<string> Bones,
        [property: JsonPropertyName("bind_poses")] string BindPoses,
        [property: JsonPropertyName("space")] string Space);

    /// <summary>A vertex channel as the codec reads it: which stream it sits in, its byte offset inside
    /// that stream's stride, its Unity format tag, and its STORED component count.</summary>
    public sealed record ChannelRecord(
        [property: JsonPropertyName("stream")] int Stream,
        [property: JsonPropertyName("offset")] int Offset,
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("dimension")] int Dimension);

    /// <summary>One submesh of the emitted index buffer.</summary>
    public sealed record SubmeshSpan(
        [property: JsonPropertyName("first_byte")] int FirstByte,
        [property: JsonPropertyName("index_count")] int IndexCount,
        [property: JsonPropertyName("base_vertex")] int BaseVertex);

    /// <summary>One shipped vertex stream: the stream NUMBER the channel table indexes it by, and the
    /// basename the mod ships it under. The number is the join, not the filename — the two routes name the
    /// same stream 0 differently (<c>combined_bind_*</c> pooled, <c>rigid_vb0_*</c> rigid), and a reader
    /// that keyed on a role word would have to know which route it was looking at.</summary>
    public sealed record StreamFile(
        [property: JsonPropertyName("stream")] int Stream,
        [property: JsonPropertyName("file")] string File);

    /// <summary>Everything needed to read a Replace's shipped buffers back into a donor payload: the files
    /// themselves, the shape they are sliced in, and the bone order their skin indices address.
    ///
    /// <para><paramref name="Streams"/> holds every vertex stream the mod actually ships, ascending; a
    /// channel naming a stream absent from it is one whose bytes did not ship. The rigid route ships no
    /// skin stream — its draw is not posed per vertex — and may ship only stream 0.</para></summary>
    public sealed record GeometryRecord(
        [property: JsonPropertyName("streams")] IReadOnlyList<StreamFile> Streams,
        [property: JsonPropertyName("index_file")] string IndexFile,
        [property: JsonPropertyName("verts")] int Verts,
        [property: JsonPropertyName("index_format")] string IndexFormat,
        [property: JsonPropertyName("submeshes")] IReadOnlyList<SubmeshSpan> Submeshes,
        [property: JsonPropertyName("channels")] IReadOnlyList<ChannelRecord> Channels,
        [property: JsonPropertyName("anchor")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Anchor = null,
        [property: JsonPropertyName("pool")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Pool = null,
        [property: JsonPropertyName("union")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UnionRecord? Union = null,
        [property: JsonPropertyName("group_slots")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GroupSlot>? GroupSlots = null);

    /// <summary>One change the mod ships, under the identity the project keys it by plus the identity of
    /// the game asset it was pointed at. <paramref name="Suffix"/> is the name this change's shipped files
    /// carry, and the join between this record and the folder around it.</summary>
    public sealed record ChangeRecord(
        [property: JsonPropertyName("verb")] string Verb,
        [property: JsonPropertyName("character")] string Character,
        [property: JsonPropertyName("outfit")] string Outfit,
        [property: JsonPropertyName("mesh")] string Mesh,
        /// <summary>The logical bundle the target's mesh resolved to on the building install. Absent when
        /// it did not resolve — an absent identity is a fact a reader can act on, an empty one is not.</summary>
        [property: JsonPropertyName("bundle")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Bundle,
        [property: JsonPropertyName("path_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PathId = null,
        [property: JsonPropertyName("bundle_content")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BundleContent = null,
        [property: JsonPropertyName("suffix")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Suffix = null,
        [property: JsonPropertyName("route")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Route = null,
        [property: JsonPropertyName("toggle_key")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KeyBinding? ToggleKey = null,
        [property: JsonPropertyName("key_groups")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<KeyGroupRecord>? KeyGroups = null,
        [property: JsonPropertyName("baked_rest")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<float>? BakedRest = null,
        [property: JsonPropertyName("original_verts")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OriginalVerts = null,
        [property: JsonPropertyName("donor_materials")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DonorMaterials = null,
        [property: JsonPropertyName("geometry")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeometryRecord? Geometry = null,
        [property: JsonPropertyName("textures")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SubmeshRecord>? Textures = null,
        [property: JsonPropertyName("intent")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IntentRecord? Intent = null);

    /// <summary>One toon ramp the mod binds on a material of a part it does NOT replace. It is no
    /// <see cref="ChangeRecord"/>: nothing about the part's geometry or its pictures moves, so there is no
    /// verb, no suffix and no shipped geometry — only which material shades with which file. Identity is
    /// the MATERIAL, exactly as the project records the pick, since the runtime's texture hash reads too
    /// little of a ramp to tell two of them apart.
    ///
    /// <para><paramref name="Ramp"/> is the shipped <c>.dds</c>'s name inside the mod folder, which is not
    /// derivable from the pick: the build names the shipped copy itself.</para></summary>
    public sealed record StockRampRecord(
        [property: JsonPropertyName("character")] string Character,
        [property: JsonPropertyName("outfit")] string Outfit,
        [property: JsonPropertyName("mesh")] string Mesh,
        [property: JsonPropertyName("material")] string Material,
        [property: JsonPropertyName("ramp")] string Ramp,
        [property: JsonPropertyName("intent")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IntentRecord? Intent = null);

    /// <summary>The whole file.</summary>
    public sealed record Payload(
        [property: JsonPropertyName("schema")] int SchemaVersion,
        [property: JsonPropertyName("game_catalog")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GameCatalog,
        [property: JsonPropertyName("app_version")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppVersion,
        [property: JsonPropertyName("toggle_key")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToggleKey,
        [property: JsonPropertyName("subjects")] IReadOnlyList<SubjectRef> Subjects,
        [property: JsonPropertyName("changes")] IReadOnlyList<ChangeRecord> Changes,
        /// <summary>The ramp picks this mod SHIPS. Absent where it ships none — a mod built before picks
        /// existed and one whose picks were all held back write the same nothing, which is what they both
        /// carry.</summary>
        [property: JsonPropertyName("stock_ramps")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<StockRampRecord>? StockRamps = null,
        [property: JsonPropertyName("intent_assets")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<IntentAssetRecord>? IntentAssets = null);

    // ---- writing ----------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Serialize and write <paramref name="payload"/> to <see cref="FileName"/> under
    /// <paramref name="modDir"/>.</summary>
    public static void Write(string modDir, Payload payload) =>
        File.WriteAllText(Path.Combine(modDir, FileName), JsonSerializer.Serialize(payload, Json));

    /// <summary>Consumption boundary for the future Import route. Released schema-1 records remain
    /// readable with null intent metadata; schema 2 carries normalized authored records.</summary>
    public static Payload Read(string path)
    {
        string file = Directory.Exists(path) ? Path.Combine(path, FileName) : path;
        if (!File.Exists(file)) throw new FileNotFoundException($"repair data not found: {file}", file);
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(file), Json); }
        catch (JsonException e) { throw new InvalidDataException($"repair data is not valid JSON: {file}", e); }
        if (payload is null) throw new InvalidDataException($"repair data is empty: {file}");
        if (payload.SchemaVersion is not (LegacySchema or Schema))
            throw new InvalidDataException($"unsupported repair-data schema {payload.SchemaVersion}");
        if (payload.Subjects is null || payload.Changes is null)
            throw new InvalidDataException("repair data has no subject or change list");
        if (payload.SchemaVersion == Schema
            && (payload.Changes.Any(change => change.Intent is null)
                || payload.StockRamps?.Any(ramp => ramp.Intent is null) == true))
            throw new InvalidDataException("schema-2 repair data has a shipped change without intent metadata");
        if (payload.SchemaVersion == Schema)
        {
            var assetIds = (payload.IntentAssets ?? Array.Empty<IntentAssetRecord>())
                .Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
            var bindings = payload.Changes.SelectMany(change => change.Intent!.Bindings)
                .Concat(payload.StockRamps?.SelectMany(ramp => ramp.Intent!.Bindings)
                    ?? Enumerable.Empty<IntentBindingRecord>()).ToList();
            string? missing = bindings.SelectMany(binding => new[]
                {
                    binding.RequestedProjectAssetId,
                    binding.EffectiveProjectAssetId,
                }).FirstOrDefault(id => id is not null && !assetIds.Contains(id));
            if (missing is not null)
                throw new InvalidDataException(
                    $"schema-2 repair data references missing intent asset '{missing}'");
            // A key-group record whose numbers disagree with its own state list describes a group nothing
            // could have produced. Refused here rather than read: a reader taking the counts on trust would
            // show a position that is not there, or file this change's content under the wrong one.
            foreach (var change in payload.Changes)
            {
                foreach (var group in change.KeyGroups ?? Array.Empty<KeyGroupRecord>())
                {
                    string at = $"'{change.Mesh}'";
                    if (group.States is not { Count: > 0 } states)
                        throw new InvalidDataException($"schema-2 key group for {at} names no positions");
                    if (group.StateCount != states.Count)
                        throw new InvalidDataException($"schema-2 key group for {at} claims "
                            + $"{group.StateCount} positions and lists {states.Count}");
                    if (group.StateIndex < 0 || group.StateIndex >= group.StateCount)
                        throw new InvalidDataException($"schema-2 key group for {at} carries position "
                            + $"{group.StateIndex} of {group.StateCount}");
                    if (group.StartState < 0 || group.StartState >= group.StateCount)
                        throw new InvalidDataException($"schema-2 key group for {at} launches at position "
                            + $"{group.StartState} of {group.StateCount}");
                }
            }
        }
        return payload;
    }

    // ---- shaping helpers --------------------------------------------------------------------------

    /// <summary>Bone hashes ride as DECIMAL STRINGS: they are full 32-bit unsigned values, and a reader
    /// taking them as JSON numbers through a signed int32 would wrap the top half of the space.</summary>
    public static string Bone(uint hash) => hash.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Base64 of the raw little-endian float32 bind poses, 16 per bone in the given order. Throws
    /// on a bone whose pose is not 16 floats rather than padding one out — a short pose would decode as a
    /// silently shifted table for every bone after it.</summary>
    public static string BindPoses(IReadOnlyList<float[]> poses)
    {
        var bytes = new byte[poses.Count * 16 * sizeof(float)];
        for (int b = 0; b < poses.Count; b++)
        {
            if (poses[b].Length != 16)
                throw new InvalidDataException(
                    $"bind pose {b} has {poses[b].Length} floats, not 16");
            Buffer.BlockCopy(poses[b], 0, bytes, b * 16 * sizeof(float), 16 * sizeof(float));
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The one shape of a <see cref="ProjectTarget.DonorTextures"/>-style texture set that reaches
    /// the file, with each slot's shipped file supplied by <paramref name="shipped"/> — the build's own
    /// record of which encoded <c>.dds</c> an ask landed on, since the encode collapses equal images onto
    /// one file. <paramref name="stockOf"/> answers the game texture a slot overrides, and answers null
    /// wherever nothing was — every slot of a Replace, whose donor maps bind at the anchor's draw rather
    /// than standing in for a named asset.
    ///
    /// <para>A row every slot of which asks for nothing is DROPPED: an empty row and an absent one mean the
    /// same thing (inherit everything), and writing both shapes would make two mods of one project differ
    /// on nothing.</para></summary>
    /// <param name="carriedOrigin">The origin of a slot filled by something OTHER than the modder's own
    /// choice (see <see cref="CarriedFromDonor"/>) — null wherever nothing filled one, which is what a
    /// caller with no such slots passes. It OUTRANKS the slot's own origin: a carried ramp is recorded on
    /// the row as an ask, exactly as a picked one is, and only this separates the two.</param>
    public static List<SubmeshRecord> Submeshes(IEnumerable<SubmeshTextures> sets,
        Func<SubmeshTextures, DonorMapSlot, string?> shipped,
        Func<SubmeshTextures, DonorMapSlot, StockTextureRef?>? stockOf = null,
        Func<SubmeshTextures, DonorMapSlot, string?>? carriedOrigin = null,
        Func<SubmeshTextures, string, string?>? shippedProperty = null,
        Func<SubmeshTextures, string, StockTextureRef?>? stockProperty = null)
    {
        var rows = new List<SubmeshRecord>();
        foreach (var t in sets.OrderBy(s => s.Submesh))
        {
            SlotRecord? Slot(SlotOrigin ask, DonorMapSlot which) =>
                carriedOrigin?.Invoke(t, which) is { } carried
                    ? new SlotRecord(carried, shipped(t, which), stockOf?.Invoke(t, which))
                    : ask != SlotOrigin.None
                        ? new SlotRecord(ask.ToString(), shipped(t, which), stockOf?.Invoke(t, which))
                        : null;
            var albedo = Slot(t.AlbedoAsk, DonorMapSlot.BaseColor);
            var normal = Slot(t.NormalAsk, DonorMapSlot.Normal);
            var rmo = Slot(t.RmoAsk, DonorMapSlot.Rmo);
            var ramp = Slot(t.RampAsk, DonorMapSlot.Ramp);
            var blend = Slot(t.BlendAsk, DonorMapSlot.Blend);
            var properties = (t.Textures ?? new List<PropertyTextureBinding>())
                .Where(texture => !string.IsNullOrWhiteSpace(texture.ShaderProperty)
                    && texture.Ask != SlotOrigin.None)
                .OrderBy(texture => texture.ShaderProperty, StringComparer.Ordinal)
                .Select(texture => new PropertySlotRecord(texture.ShaderProperty,
                    new SlotRecord(texture.Ask.ToString(),
                        shippedProperty?.Invoke(t, texture.ShaderProperty),
                        stockProperty?.Invoke(t, texture.ShaderProperty))))
                .ToList();
            if (albedo is null && normal is null && rmo is null && ramp is null && blend is null
                && properties.Count == 0) continue;
            rows.Add(new SubmeshRecord(t.Submesh, albedo, normal, rmo, ramp, blend,
                properties.Count > 0 ? properties : null));
        }
        return rows;
    }
}
