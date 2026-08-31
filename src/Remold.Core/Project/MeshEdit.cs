using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Remold.Core.Project;

// The vocabulary a change is described in: what a change DOES to a part, and what one submesh's map slots
// ask for. Every type in this file is SHARED, not legacy — the runtime compiler's own work items are
// written in these words (Migoto.BuildWorkItem holds SubmeshTextures, SlotOrigin, CarriedRamp,
// RmoAlphaAnswer and an EditVerbs string), and a schema-1 manifest happens to use the same ones, which is
// what lets the conversion read one without translating. The file keeps the name of the entry type it used
// to hold: that was the released build's derived edit list, and it is gone.

/// <summary>The verbs a change can carry.</summary>
public static class EditVerbs
{
    /// <summary>Swap the mesh for an authored donor, which may ride any bones of the outfit — the build
    /// derives the recovery pool from the donor's weights, not from this mesh.</summary>
    public const string Replace = "replace";
    /// <summary>Skip the mesh entirely (every pass, shadows included).</summary>
    public const string Hide = "hide";
    /// <summary>Keep the mesh, override its textures at its own draws (mesh-scoped binds).</summary>
    public const string Retexture = "retexture";
    /// <summary>Keep the mesh and every picture on it, and shade a material of it with a toon ramp the mod
    /// carries. It exists so a pick on a part with no other change has a change row, a tick and a toggle
    /// key of its own, keyed exactly as every other row is.</summary>
    public const string Ramp = "ramp";

    /// <summary>There is no "leave" verb: an untouched part carries no change at all, so a change list only
    /// ever holds work to do.</summary>
    public static bool IsValid(string? verb) => verb is Replace or Hide or Retexture or Ramp;
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
    /// <summary>The toon ramp, as a <c>.dds</c> in the game's own float format. It is the one slot with no
    /// PNG anywhere in its path: the values ARE the shading curve, so nothing quantises or re-encodes it.
    /// Absent on every project that predates ramps, which reads as "keep the one the game bound".</summary>
    [JsonPropertyName("ramp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ramp { get; set; }

    /// <summary>The effect overlay (<c>_BlendTex</c>): the hair specular band, the face blush tint. A
    /// game-domain picture like the three above it; absent on every row that predates the slot.</summary>
    [JsonPropertyName("blend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blend { get; set; }

    /// <summary>Ordinary texture bindings whose shader property has no fixed field above. Property is the
    /// binding identity; file/origin retain the same ask contract as the fixed picture fields. Optional and
    /// absent on every older row.</summary>
    [JsonPropertyName("textures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PropertyTextureBinding>? Textures { get; set; }

    [JsonPropertyName("albedo_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin AlbedoOrigin { get; set; }
    [JsonPropertyName("normal_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin NormalOrigin { get; set; }
    [JsonPropertyName("rmo_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin RmoOrigin { get; set; }
    [JsonPropertyName("ramp_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin RampOrigin { get; set; }
    [JsonPropertyName("blend_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin BlendOrigin { get; set; }

    /// <summary>Set when the ramp slot's file was CARRIED rather than chosen: the conversion followed this
    /// row's own map references back to the game material the donor geometry was shaded by and took its
    /// ramp, and this names that texture. Absent on a ramp the modder picked, and on every row that names
    /// no ramp at all — so its presence is what tells a carried pick from a chosen one, and its contents
    /// are what a repair record states the pick stood in for.
    ///
    /// <para>Write it through <see cref="SetRamp"/> only: a file with no origin recorded beside it would
    /// read back as a decision nobody made.</para></summary>
    [JsonPropertyName("ramp_carried")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CarriedRamp? RampCarried { get; set; }

    /// <summary>This row's ramp was carried by the conversion, not picked by the modder.</summary>
    [JsonIgnore] public bool RampIsCarried => Ramp is not null && RampCarried is not null;

    /// <summary>What this submesh's authored RMO alpha is for, once asked. Null = never asked, which is
    /// every row that predates the question and every row no ambiguous image ever landed on. The intake
    /// honours it on every later pass, so the modder answers once per submesh rather than per drop.</summary>
    [JsonPropertyName("rmo_alpha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RmoAlphaAnswer? RmoAlpha { get; set; }

    /// <summary>THE write route for the ramp slot, so no flow can leave the file and its provenance
    /// disagreeing. <paramref name="projectRelativeDds"/> null clears the slot back to "nothing picked";
    /// <paramref name="carried"/> non-null marks the pick as one the conversion made and names the game
    /// texture it stood in for.</summary>
    public void SetRamp(string? projectRelativeDds, CarriedRamp? carried = null)
    {
        Ramp = projectRelativeDds;
        RampOrigin = projectRelativeDds is null ? SlotOrigin.None : SlotOrigin.Authored;
        RampCarried = projectRelativeDds is null ? null : carried;
    }

    /// <summary>Record the ramp opt-out: the part keeps whatever ramp the game already binds there, and the
    /// conversion leaves this row alone from now on. Not the same as an empty slot, which the conversion is
    /// still free to fill.</summary>
    public void KeepOwnRamp()
    {
        Ramp = null;
        RampOrigin = SlotOrigin.VanillaOwn;
        RampCarried = null;
    }

    /// <summary>What the albedo slot asks for. A named file settles it, so a writer that only assigns
    /// paths (a retexture, a map-card pick) needs no origin of its own.</summary>
    [JsonIgnore] public SlotOrigin AlbedoAsk => Ask(Albedo, AlbedoOrigin);
    /// <inheritdoc cref="AlbedoAsk"/>
    [JsonIgnore] public SlotOrigin NormalAsk => Ask(Normal, NormalOrigin);
    /// <inheritdoc cref="AlbedoAsk"/>
    [JsonIgnore] public SlotOrigin RmoAsk => Ask(Rmo, RmoOrigin);
    /// <inheritdoc cref="AlbedoAsk"/>
    [JsonIgnore] public SlotOrigin RampAsk => Ask(Ramp, RampOrigin);
    /// <inheritdoc cref="AlbedoAsk"/>
    [JsonIgnore] public SlotOrigin BlendAsk => Ask(Blend, BlendOrigin);

    /// <summary><see cref="SlotOrigin.Authored"/> means a file, both ways: the two cannot be recorded in
    /// disagreement, so nothing downstream has to handle an authored slot with nothing to bind.</summary>
    private static SlotOrigin Ask(string? file, SlotOrigin recorded) =>
        file is not null ? SlotOrigin.Authored
        : recorded == SlotOrigin.Authored ? SlotOrigin.None
        : recorded;
}

/// <summary>One property-keyed ordinary picture in a build texture row.</summary>
public sealed class PropertyTextureBinding
{
    [JsonPropertyName("shader_property")] public string ShaderProperty { get; set; } = "";
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }
    [JsonPropertyName("origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SlotOrigin Origin { get; set; }

    [JsonIgnore] public SlotOrigin Ask => File is not null ? SlotOrigin.Authored
        : Origin == SlotOrigin.Authored ? SlotOrigin.None : Origin;
}

/// <summary>What the emissive mask of an authored RMO is, once the modder has said. Persisted BY NAME, so
/// the members can be reordered without rewriting manifests.
///
/// <para>Only ever recorded for the one shape that is genuinely ambiguous — an authored alpha that is
/// constant pure white, which reads equally as "I painted no mask" and as "everything here glows". Absent
/// is not a third answer: it means nothing ambiguous has arrived on this row.</para></summary>
[JsonConverter(typeof(JsonStringEnumConverter<RmoAlphaAnswer>))]
public enum RmoAlphaAnswer
{
    /// <summary>Take the mask off the part's own stock RMO, discarding the authored alpha.</summary>
    Rebuild,
    /// <summary>Ship the alpha as authored.</summary>
    ShipAsAuthored,
}

/// <summary>The game texture a CARRIED ramp stands in for, by the identity the game holds it under.
/// Recorded rather than re-derived because the references it was read off can move afterwards — a map card
/// re-authored over the link the ramp was found through leaves the geometry, and its shading, exactly as
/// they were.</summary>
public sealed class CarriedRamp
{
    [JsonPropertyName("bundle")] public string Bundle { get; set; } = "";
    /// <summary>The Texture2D's own asset name — a label, not a selector: every ramp in a ramp library is
    /// called the same thing.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>WHICH texture of that bundle, the way <see cref="ProjectTarget.PathId"/> selects a mesh.
    /// Null on a record written before ramps were selected this way — those fall back to the name, which is
    /// the identity they were carried under.</summary>
    [JsonPropertyName("path_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PathId { get; set; }
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
