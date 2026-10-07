using System.Collections.Generic;
using Remold.Core.Model;

namespace Remold.Core.Export;

/// <summary>One LOD-tier slot of a part, carried prefab-exact: the renderer slot's GameObject name
/// plus that tier's mesh identity in ONE of the two prefab forms —
/// a recipe addressable (<see cref="MeshAddress"/>) for recipe-backed parts, or a resolved
/// <see cref="MeshBundle"/> + <see cref="MeshPathId"/> for smr-body parts, whose renderers reference
/// meshes directly. Fans a part's sibling detail levels out without re-deriving a name from
/// prefix+token.
///
/// <para><see cref="CastsShadows"/> is the tier renderer's shadow-pass participation. A tier that does
/// not cast is drawn only while it is in frame, so it can witness nothing about presence; false requires
/// a measured Off.</para>
///
/// <para><see cref="Visibility"/> is the game-side override that can leave THIS tier undrawn. It is keyed
/// per tier because the dorm lists name individual tier nodes, so one tier of a part can be withheld while
/// its siblings draw.</para>
///
/// <para><see cref="Materials"/> is the tier renderer's ordered <c>m_Materials</c> as IDENTITIES only.
/// The game orders each tier's materials on its own, so position k at one tier is not position k at
/// another; matching these identities against the lod0 slot's is what says which tier position carries a
/// given lod0 region. Null when nothing filled it (a hand-built part, or a tier read before this field
/// existed).</para></summary>
public readonly record struct RecipeTierSlot(string SlotName, string MeshAddress,
    string? MeshBundle = null, long MeshPathId = 0, bool CastsShadows = true,
    VisibilityOverride Visibility = VisibilityOverride.None,
    string? RendererBundle = null, long RendererPathId = 0,
    IReadOnlyList<TierMaterialRef>? Materials = null);

/// <summary>One ordered material reference of a tier renderer, reduced to what identifies it: the logical
/// <see cref="Bundle"/> the reference resolved to and its <see cref="PathId"/>. No name and no maps — the
/// tier's materials are read for correspondence, and reading their names would open every tier material
/// asset for a question identity already answers.
///
/// <para>Three forms. A PLACEHOLDER (an empty renderer slot, PPtr 0:0) is <c>(null, 0, true)</c> and holds
/// its position; it corresponds only to another placeholder. A reference whose CAB no bundle in scope
/// provides is <c>(null, pathId, false)</c> — <see cref="Resolved"/> false — and corresponds to nothing,
/// because nothing says what it is. Everything else carries its bundle and path id.</para></summary>
public readonly record struct TierMaterialRef(string? Bundle, long PathId, bool Resolved)
{
    /// <summary>True for an empty renderer slot (PPtr 0:0) — a placeholder that holds submesh order.</summary>
    public bool IsPlaceholder => Resolved && Bundle is null && PathId == 0;
}

/// <summary>
/// The prefab-exact identity of one workbench part, so the mesh is read by exact identity, never
/// re-derived from mesh prefix + name-convention token. Two backed forms:
///
/// <para><b>Recipe-backed</b> (character/RX prefabs): <see cref="SlotName"/> is the representative
/// (<c>_lod0</c>) slot name, which the mesh object in the addressed bundle also carries as its
/// <c>m_Name</c>; <see cref="MeshAddress"/> is its recipe addressable. This is what unblocks a cross-prefix
/// part — an alt whose recipe reuses the BASE outfit's face.</para>
///
/// <para><b>SMR-backed</b> (enemy smr-body): the renderer references its mesh directly, so the identity
/// is <see cref="MeshBundle"/> + <see cref="MeshPathId"/> — enemy bundles ship same-named copies, so the
/// path id, not the name, is the selector, and the mesh object's own <c>m_Name</c> may be anything at all.
/// <see cref="SlotName"/> is still what the export records and every ledger, roster and build lookup keys
/// on.</para>
///
/// <para><see cref="SiblingTiers"/> are the part's OTHER tier slots for the LOD fan-out, each in its
/// owner's form. A part backed NEITHER way makes the exporter FAIL LOUDLY rather than fall back to name
/// matching.</para>
///
/// <para><see cref="IsStatic"/> carries the renderer class: a plain MeshRenderer slot, whose mesh has no
/// skin. It rides here because the Blender session description is written from the recipe, and the bridge's
/// weight gate has to know which parts have no weights to solve.</para>
/// </summary>
public sealed record RecipePart(
    string Token,
    string SlotName,
    string MeshAddress,
    IReadOnlyList<RecipeTierSlot> SiblingTiers,
    string? MeshBundle = null,
    long MeshPathId = 0,
    bool IsStatic = false)
{
    /// <summary>Slot name AND mesh address present — the character/RX route.</summary>
    public bool IsRecipeBacked => !string.IsNullOrEmpty(SlotName) && !string.IsNullOrEmpty(MeshAddress);

    /// <summary>Bundle + path id present — the smr-body route, checked AFTER
    /// <see cref="IsRecipeBacked"/>.</summary>
    public bool IsSmrBacked => !string.IsNullOrEmpty(MeshBundle) && MeshPathId != 0;
}
