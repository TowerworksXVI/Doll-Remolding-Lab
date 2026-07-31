using System.Collections.Generic;

namespace Remold.Core.Export;

/// <summary>One LOD-tier slot of a part, carried prefab-exact: the renderer slot's GameObject name
/// plus that tier's mesh identity in ONE of the two prefab forms —
/// a recipe addressable (<see cref="MeshAddress"/>) for recipe-backed parts, or a resolved
/// <see cref="MeshBundle"/> + <see cref="MeshPathId"/> for smr-body parts, whose renderers reference
/// meshes directly. Fans a part's sibling detail levels out without re-deriving a name from
/// prefix+token.</summary>
public readonly record struct RecipeTierSlot(string SlotName, string MeshAddress,
    string? MeshBundle = null, long MeshPathId = 0);

/// <summary>
/// The prefab-exact identity of one workbench part, carried down into
/// <see cref="AssetExporter.ExportRecipePart"/> so the mesh is read by exact identity, never re-derived
/// from mesh prefix + name-convention token. Two backed forms:
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
