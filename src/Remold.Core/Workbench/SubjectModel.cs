using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Export;

namespace Remold.Core.Workbench;

/// <summary>
/// Where a <see cref="SubjectModel"/>'s part list came from. The assembly prefab is the ONLY source: no
/// resolvable prefab means a part-less model with a loud Problems entry, never a name-guessed part list.
/// </summary>
public enum SubjectSource
{
    Prefab,
}

/// <summary>
/// One picked subject (outfit or enemy/NPC) resolved to parts (renderer slots) → materials (submesh
/// order) → texture maps, plus the skeleton. Immutable snapshot built off the UI thread by
/// <see cref="SubjectModelBuilder"/>. Two invariants: material order IS the submesh binding, so an empty
/// renderer slot becomes a placeholder <see cref="SubjectMaterial"/> named <c>""</c> and is never dropped
/// or reordered; and an unresolved part/material/map/skeleton surfaces in <see cref="Problems"/> and/or as
/// a marked error node — never a silently absent node.
/// </summary>
/// <param name="Character">The character's internal key (rendered friendly by the VM; never shown raw).</param>
/// <param name="Stem">The outfit/model stem (e.g. <c>KarstSSR01</c>).</param>
/// <param name="Source">The structure's provenance (<see cref="SubjectSource"/>).</param>
/// <param name="Parts">The renderer slots, grouped by part token (Fight/Dorm variants are distinct parts).</param>
/// <param name="Skeleton">The subject's rig, or null when it couldn't be read.</param>
/// <param name="Problems">Subject-level problems, loud and rolled up (empty on a clean read). Every
/// entry means the PART data is curtailed or suspect — consumers that need a complete part list (the
/// sharing measurement) key off this being empty.</param>
/// <param name="SkeletonProblem">Why <paramref name="Skeleton"/> is null, when it is (e.g. a bone name
/// two Transforms carry). Its own channel, not a <paramref name="Problems"/> entry: the parts stand
/// complete without a skeleton — the loss is display and scene-rig niceties, and it must not read as a
/// curtailed part list.</param>
/// <param name="PrimaryRoot">Root name of the PRIMARY candidate prefab — the skeleton source, and the
/// baseline a part's <see cref="SubjectPart.SourceRoot"/> is compared against to spot a stem-sibling
/// root. Empty when no prefab resolved.</param>
/// <param name="PrimaryBundle">Logical bundle id of that primary candidate prefab — where an smr-body
/// part's scene rig is read from when its own mesh bundle carries none. Empty when no prefab
/// resolved.</param>
public sealed record SubjectModel(
    string Character,
    string Stem,
    SubjectSource Source,
    IReadOnlyList<SubjectPart> Parts,
    SubjectSkeleton? Skeleton,
    IReadOnlyList<string> Problems,
    string PrimaryRoot = "",
    string PrimaryBundle = "",
    string? SkeletonProblem = null)
{
    /// <summary>Every part of this subject draws from a static renderer slot. A combined Blender session
    /// carries the SKINNED parts, so such a subject has none to carry and is authored one part at a time.
    /// False for a part-less model and for a MIXED subject, whose session carries its skinned parts.</summary>
    public bool AllPartsStatic => Parts.Count > 0 && Parts.All(p => p.IsStatic);
}

/// <summary>
/// One renderer slot family (all LOD tiers of a garment collapse to one part). <see cref="SlotName"/> is
/// the representative <c>_lod0</c> slot's GameObject name, <see cref="MeshAddress"/> the recipe's mesh
/// addressable (empty for a slot with no recipe entry — enemy/NPC prefab bodies), <see cref="Materials"/>
/// the slot's ordered <c>m_Materials</c> (order == submesh binding), <see cref="SourceRoot"/> the
/// candidate prefab root that supplied it (the workbench merges parts across candidates).
/// </summary>
/// <param name="SiblingTiers">The part's OTHER tier slots for the LOD fan-out, each with its own slot
/// name + mesh identity. Empty for a single-tier or orphan part; the representative <c>_lod0</c> tier is
/// never repeated here.</param>
/// <param name="MeshBundle">smr-body only — the logical bundle holding the slot's serialized mesh. Null
/// on recipe-backed parts (their address resolves at export) and on unbacked parts.</param>
/// <param name="MeshPathId">smr-body only — the mesh's path id in <see cref="MeshBundle"/> (enemy
/// bundles ship same-named copies, so the id, not the name, is the selector). 0 otherwise.</param>
/// <param name="IsStatic">The representative slot is drawn by a plain <c>MeshRenderer</c> — a draw the
/// game does not pose per vertex. Read off the prefab, so it costs no mesh read; it describes the SLOT,
/// which is what decides whether the part can join a combined rigged session.</param>
public sealed record SubjectPart(
    string Token,
    string SlotName,
    string MeshAddress,
    IReadOnlyList<SubjectMaterial> Materials,
    string SourceRoot = "",
    string? Problem = null,
    IReadOnlyList<RecipeTierSlot>? SiblingTiers = null,
    string? MeshBundle = null,
    long MeshPathId = 0,
    bool IsStatic = false)
{
    /// <summary>The prefab-exact identity carried down to <see cref="AssetExporter.ExportRecipePart"/>:
    /// representative slot + sibling tiers, recipe-backed (address) or smr-backed (bundle+path-id). A part
    /// backed neither way yields a <see cref="RecipePart"/> the exporter fails loudly on rather than
    /// falling back to name matching.</summary>
    public RecipePart ToRecipePart() =>
        new(Token, SlotName, MeshAddress, SiblingTiers ?? Array.Empty<RecipeTierSlot>(),
            MeshBundle, MeshPathId, IsStatic);
}

/// <summary>
/// One material bound by a part's renderer slot, in submesh order. <see cref="Name"/> is <c>m_Name</c>
/// (shown; <c>""</c> marks a PLACEHOLDER for an empty renderer slot).
/// <see cref="PathId"/>/<see cref="Cab"/> are the renderer's PPtr (internal, NEVER shown) — a placeholder
/// is <c>(0, null)</c>. <see cref="Problem"/> is non-null when a non-empty material reference couldn't be
/// resolved.
/// </summary>
public sealed record SubjectMaterial(
    string Name,
    long PathId,
    string? Cab,
    IReadOnlyList<SubjectMap> Maps,
    string? Problem = null)
{
    /// <summary>True for an empty renderer slot (PPtr 0:0) — a placeholder that holds submesh order.</summary>
    public bool IsPlaceholder => PathId == 0 && Cab is null;
}

/// <summary>One texture a material binds: shader <see cref="Slot"/> (<c>_BaseMap</c>/<c>_BumpMap</c>/…),
/// the Texture2D <see cref="TextureName"/>, and the logical <see cref="BundleId"/> (never shown). Texture
/// dimensions are NOT read at build time; the inspector reads them lazily on node selection.</summary>
public sealed record SubjectMap(string Slot, string TextureName, string BundleId);

/// <summary>The subject's rig, read from the assembly prefab's Transform hierarchy. <see cref="Bones"/> is
/// in read order with parent links. An unreadable hierarchy yields a null skeleton plus a loud Problems
/// entry, never a silent 0.</summary>
public sealed record SubjectSkeleton(IReadOnlyList<SubjectBone> Bones)
{
    public int BoneCount => Bones.Count;
}

/// <summary>One bone of a <see cref="SubjectSkeleton"/>: its GameObject name and the INDEX of its parent
/// within the same bone list (<c>-1</c> for a root or a parent outside the read set).</summary>
public sealed record SubjectBone(string Name, int ParentIndex);
