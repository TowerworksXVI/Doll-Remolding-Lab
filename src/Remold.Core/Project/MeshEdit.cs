using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Remold.Core.Project;

/// <summary>The per-mesh verbs of the derived edit list.</summary>
public static class EditVerbs
{
    /// <summary>Swap the mesh for an authored donor, which may ride any bones of the outfit — the build
    /// derives the recovery pool from the donor's weights, not from this mesh.</summary>
    public const string Replace = "replace";
    /// <summary>Skip the mesh entirely (every pass, shadows included).</summary>
    public const string Hide = "hide";
    /// <summary>Keep the mesh, override its textures at its own draws (mesh-scoped binds).</summary>
    public const string Retexture = "retexture";

    /// <summary>There is no "leave" verb: an untouched mesh has no <see cref="MeshEdit"/> entry at all, so
    /// the edit list only ever holds work to do.</summary>
    public static bool IsValid(string? verb) => verb is Replace or Hide or Retexture;
}

/// <summary>
/// One entry of the build's <b>derived edit list</b>. NEVER persisted or authored:
/// <see cref="Workbench.VerbDerivation"/> rebuilds the list in memory each build, so the verbs can't
/// drift from what the workbench shows. Identity is (<see cref="Character"/>, <see cref="Outfit"/>,
/// <see cref="Mesh"/>); a mesh with no entry is untouched. All file paths are workspace-relative.
/// </summary>
public sealed class MeshEdit
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    /// <summary>The outfit stem — matches <see cref="SelectionEntry.Outfit"/>.</summary>
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    /// <summary>The roster part's renderer slot name (its representative <c>_lod0</c> slot). LOD-tier
    /// fan-out is NOT stored here — the build enumerates tiers from the live recipe, so this can't go
    /// stale against a game update.</summary>
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";
    /// <summary>Exact selector for smr-backed meshes (same-named copies per bundle); null on
    /// recipe-backed meshes, where the name selects.</summary>
    [JsonPropertyName("path_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PathId { get; set; }

    /// <summary>One of <see cref="EditVerbs"/>.</summary>
    [JsonPropertyName("verb")] public string Verb { get; set; } = "";

    /// <summary>Replace only — the authored donor glb, weighted to the outfit's reference armature. Null
    /// is allowed by the data layer (verb picked, donor not yet imported) and refused loudly at
    /// build.</summary>
    [JsonPropertyName("donor_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DonorFile { get; set; }

    /// <summary>Replace: per-donor-submesh texture set. Retexture: per-VANILLA-submesh. A submesh with no
    /// entry INHERITS every slot: they are left alone and the part's own stock maps keep drawing there.
    /// On an entry, what a slot without a file does is its <see cref="SlotOrigin"/>'s to say.</summary>
    [JsonPropertyName("textures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubmeshTextures>? Textures { get; set; }

    /// <summary>Replace only — explicit anchor pool-part name overriding the build's derived pick.</summary>
    [JsonPropertyName("anchor_override")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnchorOverride { get; set; }

    /// <summary>Replace only — the target's recorded scene-rest uprighting (see
    /// <see cref="Mesh.RestBake"/>). The workspace donor is in scene-rest space; the build un-bakes it
    /// by this before compiling the game-space payload. Null = nothing baked.</summary>
    [JsonPropertyName("baked_rest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<float>? BakedRest { get; set; }

    /// <summary>Whether this entry is for the given subject, case-insensitive — the same comparison as
    /// <see cref="ModProject.HasSubject"/>.</summary>
    public bool IsForSubject(string character, string stem) =>
        string.Equals(Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Outfit, stem, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every workspace file this edit references — what the build existence-checks before
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
            }
    }
}

/// <summary>What one map slot of one submesh asks the build for. A slot naming a file is
/// <see cref="SlotOrigin.Authored"/> whatever else is recorded, so the three file-less cases are the only
/// ones this has to tell apart. Persisted BY NAME, so the members can be reordered without rewriting manifests.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlotOrigin>))]
public enum SlotOrigin
{
    /// <summary>No image on the slot at all.</summary>
    None,
    /// <summary>An authored image ships and binds. The slot names the file.</summary>
    Authored,
    /// <summary>The modder plugged the shipped neutral in. Nothing ships: the build binds its own flat
    /// map, blanking the slot.</summary>
    ExplicitNeutral,
    /// <summary>The part's own stock image came back untouched. Nothing ships and nothing binds: the real
    /// map keeps drawing.</summary>
    VanillaOwn,
}

/// <summary>The texture maps for one submesh, each with the ORIGIN that says what the modder asked for on
/// that slot. These are the author's source files (PNG or DDS) — the build owns the encode (albedo sRGB,
/// normal/RMO linear).</summary>
public sealed class SubmeshTextures
{
    [JsonPropertyName("submesh")] public int Submesh { get; set; }
    [JsonPropertyName("albedo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Albedo { get; set; }
    [JsonPropertyName("normal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Normal { get; set; }
    [JsonPropertyName("rmo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rmo { get; set; }

    [JsonPropertyName("albedo_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin AlbedoOrigin { get; set; }
    [JsonPropertyName("normal_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin NormalOrigin { get; set; }
    [JsonPropertyName("rmo_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin RmoOrigin { get; set; }

    /// <summary>What the albedo slot asks for. A named file settles it, so a writer that only assigns
    /// paths (a retexture, a map-card pick) needs no origin of its own.</summary>
    [JsonIgnore] public SlotOrigin AlbedoAsk => Ask(Albedo, AlbedoOrigin);
    /// <inheritdoc cref="AlbedoAsk"/>
    [JsonIgnore] public SlotOrigin NormalAsk => Ask(Normal, NormalOrigin);
    /// <inheritdoc cref="AlbedoAsk"/>
    [JsonIgnore] public SlotOrigin RmoAsk => Ask(Rmo, RmoOrigin);

    /// <summary><see cref="SlotOrigin.Authored"/> means a file, both ways: the two cannot be recorded in
    /// disagreement, so nothing downstream has to handle an authored slot with nothing to bind.</summary>
    private static SlotOrigin Ask(string? file, SlotOrigin recorded) =>
        file is not null ? SlotOrigin.Authored
        : recorded == SlotOrigin.Authored ? SlotOrigin.None
        : recorded;
}

/// <summary>The one place that decides which origins are an ask, so the intake, the build and the
/// send-back summary cannot drift apart on it.</summary>
public static class SlotOrigins
{
    /// <summary>Whether the slot carries an ask of its own. Leaving a stock map alone, or having no image
    /// at all, does not — which is what separates "blank this" from "don't touch this".</summary>
    public static bool IsAsk(this SlotOrigin origin) =>
        origin is SlotOrigin.Authored or SlotOrigin.ExplicitNeutral;
}

/// <summary>Which of a submesh row's three map slots ship the build's own FLAT map rather than an image or
/// the part's stock one — the state the Edit card and the change-list chip both call <i>blanked</i>. ONE
/// statement of the rule: the emission asks it, and so does everything that describes the emission, or a
/// pane says a slot is untouched while the build blanks it.
///
/// <para>Only normal and RMO have a flat map. Two ways one goes flat: an
/// <see cref="SlotOrigin.ExplicitNeutral"/> slot was blanked on purpose, under any verb; and under
/// <see cref="EditVerbs.Replace"/> a submesh that asked for anything at all takes the flat map on whichever of
/// the two named no image of its own, since that submesh draws on donor UVs and the part's real relief sampled
/// through foreign UVs reads as garbage. A retexture keeps the vanilla UVs and takes no such
/// substitution.</para>
///
/// <para><see cref="Albedo"/> is therefore always false: no flat albedo exists to stand in for a base color,
/// and a row whose albedo asks for the neutral is refused at build rather than blanked.</para></summary>
public readonly record struct BlankedSlots(bool Albedo, bool Normal, bool Rmo)
{
    /// <summary>The row's flat slots under <paramref name="verb"/>. A row with no entry at all is not this
    /// question: every slot of an absent submesh inherits.</summary>
    public static BlankedSlots Of(SubmeshTextures t, string verb)
    {
        bool reliefDue = verb == EditVerbs.Replace
            && (t.AlbedoAsk.IsAsk() || t.NormalAsk.IsAsk() || t.RmoAsk.IsAsk());
        return new BlankedSlots(
            Albedo: false,
            Flat(t.NormalAsk, reliefDue),
            Flat(t.RmoAsk, reliefDue));
    }

    private static bool Flat(SlotOrigin ask, bool reliefDue) =>
        ask == SlotOrigin.ExplicitNeutral || (reliefDue && ask == SlotOrigin.None);
}
