using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Skeleton;

namespace Remold.Core.Migoto;

/// <summary>One pooled part: the identifier used in filenames and ini sections, its mesh-dump dir, and
/// <paramref name="OpKey"/> — the source mesh's stable game-side identity for operator-cache reuse (null =
/// solve fresh, keep nothing). <paramref name="MeasuredRest"/> is the part's measured bind→scene transform
/// (see <see cref="Skeleton.SceneRig.MeasuredRest"/>); the bind-space reconciliation MUST prefer measured
/// deltas exactly as <see cref="SwapCompile.BuildUnionOrder"/> does — the compiled donor streams and the
/// emitted palette state one union space. <paramref name="RootChain"/> is the bone the part's renderer is
/// rooted at and that bone's ancestors, as bone hashes, the root first and the skeleton's top last; null
/// when the renderer is unknown or its rig could not be read. A pooled Replace tells copies of this part
/// apart by where that root stands (see <see cref="MigotoEmitter.PoseRingEntries"/>).</summary>
public readonly record struct PoolPart(string Name, string DumpDir, string? OpKey = null,
    System.Numerics.Matrix4x4? MeasuredRest = null, IReadOnlyList<uint>? RootChain = null);

/// <summary>
/// One Replace's pipeline within a build: an ordered pool of parts, an optional donor stream dir
/// (null = identity build that concatenates the pool parts into the character's own body), per-part
/// capture hashes, an anchor part, and per-submesh map binds. Everything this pipeline owns is
/// namespaced by <see cref="Suffix"/>; what is shared BETWEEN pipelines (a part's posed/vs-cb1 captures,
/// its cpinv operator and stamped recover shader) is keyed by part name alone, so a part in two pools is
/// captured once and recovered into each pipeline's own palette.
/// </summary>
public sealed record ReplacePipeline
{
    /// <summary>Ini/file namespace for this Replace (e.g. the replaced part's name). Must be unique
    /// per build and ini-safe (lowercase alphanumerics + <c>_</c>).</summary>
    public required string Suffix { get; init; }

    /// <summary>Ordered pool parts. The last is the anchor unless <see cref="Anchor"/> overrides. The
    /// convert pass binds each part's constants at a register of b5..b12 (b13 is the anchor's), so it runs
    /// one dispatch per <see cref="ComputeTemplates.PartsPerConvert"/> parts in this order.</summary>
    public required IReadOnlyList<PoolPart> Parts { get; init; }

    /// <summary>Donor body streams dir (stream0/1/2 + ib + meta.json, weighted to the union bone order).
    /// Null = identity build.</summary>
    public string? DonorDir { get; init; }

    /// <summary>THIS pipeline's pool parts and the ib hash each captures at (part name → hash). A part
    /// without one gets a placeholder hash. Entries for parts this pipeline doesn't pool are never read, so
    /// a dictionary shared across pipelines states more than the record means.</summary>
    public IReadOnlyDictionary<string, string>? CaptureHashes { get; init; }

    /// <summary>Anchor part name (hosts convert+skin+draw); null = the last pool part.</summary>
    public string? Anchor { get; init; }

    /// <summary>Per-submesh map binds (submesh index → its three slots). A submesh with NO entry binds
    /// nothing at all, so the anchor's own stock maps keep drawing on it; within an entry each slot decides
    /// on its own. See <see cref="MapSlot"/>.</summary>
    public IReadOnlyDictionary<int, SubmeshMaps>? SubTextures { get; init; }

    /// <summary>Pool parts whose vanilla draw KEEPS running — captured for recovery, not suppressed
    /// ("Leave"). Null/empty = every pool part is skipped; capture works either way. When pools overlap,
    /// a part suppressed by ANY pipeline skips: the merged capture section's skip is the OR.</summary>
    public IReadOnlyCollection<string>? NoSkipParts { get; init; }

    /// <summary>The anchor part's own stock maps, hashed offline (the same 3DMigoto resource hash the
    /// retexture path keys on), one entry per distinct texture. Each is marked with a per-kind
    /// <c>filter_index</c> so the draw list can ask which <c>ps-t</c> slot holds the anchor's
    /// albedo/normal/RMO at the moment of the draw — the slot layout is a property of the bound pixel
    /// shader, so reading bound state needs no shader table. Null/empty is allowed; a donor-textured
    /// pipeline without an albedo tag draws geometry-only and the builder warns.</summary>
    public IReadOnlyList<StockMapTag>? StockMaps { get; init; }

    /// <summary>The anchor's ordinary property-keyed stock textures. Unlike the fixed semantic kinds,
    /// each row carries its exact shader property and its measured register candidates.</summary>
    public IReadOnlyList<StockPropertyTag>? StockProperties { get; init; }

    /// <summary>Pool parts' other LOD tiers. A suppressed part's tier is replaced the same way as its
    /// lod0 — LOD choice is not distance-only, so a merely-hidden tier would blank the character in every
    /// context that picks it. Each tier gets its own capture (skip) + recovery operator, and the anchor's
    /// tiers each run the full recover→convert→skin→draw chain. A NoSkip part's tier is captured WITHOUT
    /// skip: in a frame rendering only that tier the part's lod0 capture never fires, and an uncaptured
    /// recovery input would pose its owned bones with garbage. Tier chains use the constants-free WITNESS
    /// convert: tier renderers can draw at per-part spaces differing from lod0's, and their vs-cb1 can be
    /// a window into a shared buffer a resource copy reads wrongly, so no CB is captured for tiers.</summary>
    public IReadOnlyList<PoolTier>? Tiers { get; init; }

    /// <summary>This Replace's own toggle key (tier 2) at the position of its group's cycle this content
    /// answers. Null = no key, and the pipeline's suppression and draw run unconditionally — the emission
    /// that predates keys. A bare key string means position 0, which is where a two-state group's content
    /// sits. See <see cref="ModKeys"/>.</summary>
    public KeyRef? ToggleKey { get; init; }

    /// <summary>The hider flag suppressing this content while ANOTHER group stands in a state that takes
    /// the part off screen (<see cref="HiddenFlag.Name"/>). The draw gate conjoins the flag reading 0, so
    /// hidden outranks content. Null = no other group hides this part.</summary>
    public string? HiddenBy { get; init; }

    /// <summary>The content flag standing at 1 while ANY position answering with this change holds
    /// (<see cref="ShownFlag.Name"/>). Set only where the change answers more than one position, and then
    /// <see cref="ToggleKey"/> is null: the flag IS the content condition. Null = the change answers one
    /// position, which its own key term states.</summary>
    public string? ShownBy { get; init; }

    /// <summary>Extra states whose position demands ONE pool part's vanilla draw suppressed on top of
    /// the content gate: this group's own states answering that part hidden, and every other group's state
    /// that takes it off screen. Each emits its own guarded skip, so suppression is the OR across them.
    ///
    /// <para>Keyed by pool part because hiding is a fact about ONE part. The pipeline's own gate covers
    /// its whole pool — every part it pools is suppressed while the swap draws — but a state that hides
    /// the replaced part must leave this pipeline's pool MATES drawing their own vanilla, so their skips
    /// cannot borrow these gates. Empty/null where the content gate alone decides, and unused where
    /// <see cref="HideWhenOff"/> already suppresses in every position of the cycle.</para></summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KeyRef>>? SuppressWhen { get; init; }

    /// <summary>What this Replace leaves on screen while <see cref="ToggleKey"/> is off: <c>false</c> =
    /// the vanilla part draws again (suppression shares the donor draw's key), <c>true</c> = nothing draws
    /// there (only the donor draw carries the key). Reaches only suppressed parts; <see cref="NoSkipParts"/>
    /// keep their vanilla draw. The mod's tier-1 key stays on BOTH gates, so mod-off always returns the
    /// vanilla character. Inert without a <see cref="ToggleKey"/>.</summary>
    public bool HideWhenOff { get; init; }

    /// <summary>The presence latch (<see cref="WitnessLatch.Name"/>) gating this pipeline's suppression
    /// and draw chain, for an anchor other outfits also draw. Null = ungated. Captures stay ungated
    /// either way.</summary>
    public string? Latch { get; init; }

    /// <summary>The coverage group answering for this Replace's donor bones that no pool part poses
    /// (<see cref="PoolDerive.VariantGroups"/>): the bones with an on-screen poser in every
    /// variant×context state the target displays in. Per pipeline, because the group is formed against
    /// this Replace's own target and candidate set. Null/empty = the pool poses every bone the donor
    /// rides.</summary>
    public IReadOnlyList<PoolGroup>? Groups { get; init; }

    /// <summary>Bone hash → '/'-joined skeleton path (parents first), for whatever bones the caller could
    /// read off the subject's scene rigs. Feeds the TIE UNDERLAY: a donor-used union bone another part
    /// owns is filled with its nearest anchor-owned ancestor's row while that part's presence latch is
    /// down, so a source the scene state never renders leaves a rigid ride instead of a bind-pose seed.
    /// Null/missing bones get no tie — the underlay reseeds them to identity instead, named in the
    /// build log.</summary>
    public IReadOnlyDictionary<uint, string>? BonePaths { get; init; }

    /// <summary>The bind each bone the replacement may ride is stated under (<see cref="Mesh.BindReference"/>),
    /// stated for the REPLACED part as its Blender export states it, in the space the donor's vertices are
    /// compiled in; the anchor's own restatement carries it to where the dumps' binds are, as it does in the
    /// donor compile. Every mesh this pipeline recovers rows from, the anchor included, has the rows of a bone
    /// it binds differently converted onto it; a bone missing here ships as its mesh states it. The donor
    /// compile is handed the same one.</summary>
    public IReadOnlyDictionary<uint, Matrix4x4>? ReferenceBinds { get; init; }

    /// <summary>Extra presence-sighting ib hashes per pool part: tiers the builder DROPPED from
    /// <see cref="Tiers"/> (unreadable, ambiguous, claim-refused) whose vanilla draw keeps running. The
    /// part's latch must still see those draws, or the tie underlay would fire — a rigid ride — while
    /// the part is visibly on screen articulating.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? PresenceHashes { get; init; }

    /// <summary>The ANCHOR part's vanilla lod0 draw-shape set. With two or more submeshes, the donor's
    /// draw is routed per submesh: donor range k draws only at the vanilla draw matching submesh k's
    /// shape, so each range renders under its own material's bound state instead of every range drawing
    /// at every material's draw. Null (or a single submesh) keeps the draw in the capture section.</summary>
    public DrawShapeSet? AnchorShapes { get; init; }
}

/// <summary>The coverage group as a build carries it: its id (<see cref="PoolDerive.CoverageGroupId"/>),
/// the bones it certifies (ascending by hash), and the member parts in roster order, each once. Whichever
/// members a frame draws write the rows for the group bones they pose, and overlapping writers land the
/// same correct transform.</summary>
public sealed record PoolGroup(long SlotId, IReadOnlyList<uint> GroupBones,
    IReadOnlyList<PoolGroupMember> Members);

/// <summary>One member part of a <see cref="PoolGroup"/>: its own wardrobe variant, the scenes it is on
/// screen in, its part token, and its mesh slot name. <paramref name="VariantId"/> is metadata the
/// emission never dispatches on — a member's writes fire at its own draws, which is what already tracks
/// the worn state.
///
/// <para><see cref="Meshes"/> are the member's captured draws — lod0 first, then every renderable tier the
/// caller could claim a dump and a capture hash for. A member carrying none contributes no section: the
/// group's rows then come from whichever other member is on screen, and the emission says so. <see
/// cref="MeasuredRest"/> is the member's measured bind→scene transform, read exactly as
/// <see cref="PoolPart.MeasuredRest"/> is — the member's recovered rows and the donor's vertices must
/// state one bind space.</para></summary>
public sealed record PoolGroupMember(long VariantId, PresenceContext Context, string Token, string Mesh)
{
    public IReadOnlyList<PoolGroupMesh>? Meshes { get; init; }

    public System.Numerics.Matrix4x4? MeasuredRest { get; init; }

    /// <summary>This member's own draws are SUPPRESSED in every session state, the way a hidden pool part's
    /// are — the capture and the rebase still run at them, which is what a hidden recovery source has
    /// always done. Default is the ordinary member, whose vanilla draw keeps rendering.</summary>
    public bool Hidden { get; init; }

    /// <summary>The key positions this member's draws are suppressed IN, where a key group hides it in some
    /// of its states rather than all of them. One guarded skip per position, and the member keeps drawing
    /// in every other. Null or empty says nothing — <see cref="Hidden"/> is the every-state answer.</summary>
    public IReadOnlyList<KeyRef>? HiddenWhen { get; init; }
}

/// <summary>One captured draw of a <see cref="PoolGroupMember"/>: the unique emission name, the ib hash its
/// capture keys on, the dump dir its operator is solved from, and <paramref name="OpKey"/> — the source
/// mesh's stable game-side identity for operator-cache reuse, as on <see cref="PoolPart"/>.
/// <paramref name="Lod"/> is the tier label, EMPTY for the member's lod0: lod0 rebases from draw constants
/// and a tier from witness geometry, and that is the only thing the distinction decides.</summary>
public sealed record PoolGroupMesh(string Name, string Lod, string DumpDir, string CaptureHash,
    string? OpKey = null)
{
    /// <summary>This is the member's own lod0 draw, whose vs-cb1 a whole-resource copy reads soundly.</summary>
    public bool IsLod0 => Lod.Length == 0;
}

/// <summary>A rigid replacement's tier mesh, as the emitter needs it to check the donor's streams against
/// the layout that tier's draw reads them through.</summary>
public sealed record RigidTierLayout(string Mesh, IReadOnlyList<Remold.Core.Mesh.UnityMesh.ChannelDef> Channels);

/// <summary>
/// One RIGID replacement: a direct geometry swap at a draw the game does not pose per vertex. The compiled
/// donor's streams stand in for the vanilla ones and its submeshes are drawn in their place — no capture,
/// no palette recovery, no compute pass. Everything is per-draw; nothing is shared between replacements.
/// </summary>
public sealed record RigidReplace
{
    /// <summary>Names this replacement's shipped resources; unique across the whole build.</summary>
    public required string Suffix { get; init; }

    /// <summary>The emission name of the part this replaces, as the mod.ini header names it; the suffix
    /// where none is given.</summary>
    public string? Part { get; init; }

    /// <summary>The compiled donor dir: <c>stream*.buf</c> + <c>ib.buf</c> + <c>meta.json</c>, already in
    /// the replaced part's OWN vertex layout (see <see cref="SwapCompile.CompilePart"/>).</summary>
    public required string DonorDir { get; init; }

    /// <summary>The replaced draw's index-buffer hash — the section that suppresses it and draws the donor
    /// in its place.</summary>
    public required string Hash { get; init; }

    /// <summary>The part's OTHER shipped tiers by ib hash. Each gets the same suppression and the same
    /// donor draw: LOD choice is not distance-only, so a tier left alone would draw the stock mesh in
    /// every context that picks it.</summary>
    public IReadOnlyList<string>? TierHashes { get; init; }

    /// <summary>Per <see cref="TierHashes"/> entry, that tier mesh's name and channel table. The donor
    /// draw at a tier is read through the tier mesh's input layout, so a tier storing a stream
    /// differently from the replaced part gets that stream re-encoded. Every tier hash needs an entry:
    /// the emission throws for one it cannot check.</summary>
    public IReadOnlyDictionary<string, RigidTierLayout>? TierLayouts { get; init; }

    /// <summary>Per-donor-submesh texture binds; null when every submesh keeps the part's stock maps.</summary>
    public IReadOnlyDictionary<int, SubmeshMaps>? SubTextures { get; init; }

    /// <summary>The replaced part's own stock maps, tagged so the draw's slot probe can find them.</summary>
    public IReadOnlyList<StockMapTag>? StockMaps { get; init; }

    /// <inheritdoc cref="ReplacePipeline.StockProperties"/>
    public IReadOnlyList<StockPropertyTag>? StockProperties { get; init; }

    /// <summary>This change's tier-2 toggle key at the position of its group's cycle this content answers,
    /// or null when it carries none.</summary>
    public KeyRef? ToggleKey { get; init; }

    /// <inheritdoc cref="ReplacePipeline.HiddenBy"/>
    public string? HiddenBy { get; init; }

    /// <inheritdoc cref="ReplacePipeline.ShownBy"/>
    public string? ShownBy { get; init; }

    /// <inheritdoc cref="ReplacePipeline.SuppressWhen"/>
    public IReadOnlyList<KeyRef>? SuppressWhen { get; init; }

    /// <summary>What this replacement leaves on screen while <see cref="ToggleKey"/> is off: <c>false</c> =
    /// the part's own draw runs again, <c>true</c> = nothing draws there (only the donor draw carries the
    /// key). The mod's tier-1 key stays on both gates, so mod-off always returns the vanilla draw. Inert
    /// without a <see cref="ToggleKey"/>.</summary>
    public bool HideWhenOff { get; init; }

    /// <summary>The owning outfit's presence latch, when its draws are shared with another outfit.</summary>
    public string? Latch { get; init; }

    /// <summary>Every ib hash this replacement owns a section for.</summary>
    public IEnumerable<string> Hashes =>
        new[] { Hash }.Concat(TierHashes ?? Array.Empty<string>());

    /// <summary>Vanilla draw-shape sets per owned hash (<see cref="Hash"/> and tiers). A hash with a
    /// multi-submesh set routes the donor draw per submesh, as <see cref="ReplacePipeline.AnchorShapes"/>
    /// does for a pooled draw; a hash with no entry (or one submesh) keeps the draw in its section.</summary>
    public IReadOnlyDictionary<string, DrawShapeSet>? ShapesByHash { get; init; }

    /// <summary>Material maps per TIER hash: which of that tier's material positions carries each lod0
    /// position's region. <see cref="Hash"/> is the lod0 draw itself and takes no map. A hash with no entry
    /// routes donor ranges by position, which is what a caller with no prefab material data can say.</summary>
    public IReadOnlyDictionary<string, TierMaterialMap>? MapsByHash { get; init; }
}

/// <summary>
/// The full input surface for a swap build: one <see cref="ReplacePipeline"/> per pooled Replace verb and
/// one <see cref="RigidReplace"/> per rigid one, plus the build-wide hide hashes and appended retexture
/// sections. Shared emission (pass flags, save-slot resources, neutral maps) is emitted once regardless of
/// pipeline count.
/// </summary>
public sealed record PoolBuildRequest
{
    /// <summary>The pooled Replace pipelines, in a stable caller-chosen order (rebuild reproducibility).
    /// Suffixes unique. May be empty when the build's Replaces are all rigid.</summary>
    public required IReadOnlyList<ReplacePipeline> Pipelines { get; init; }

    public required string OutDir { get; init; }

    /// <summary>The rigid Replaces, in a stable caller-chosen order. Suffixes unique across these AND
    /// <see cref="Pipelines"/>, since both name shipped files by suffix.</summary>
    public IReadOnlyList<RigidReplace>? Rigids { get; init; }

    /// <summary>ib hashes of outfit meshes to skip. Must not repeat any pipeline's capture hash —
    /// the runtime runs every section whose match filters pass on a hash, so a hide beside a capture
    /// would act on the same draws the capture's skip already covers.</summary>
    public IReadOnlyList<string>? HideHashes { get; init; }

    /// <summary>The part each mesh section key names, for refusals that have to say which row to leave
    /// out. A key with no entry is named by its ib hash.</summary>
    public IReadOnlyDictionary<string, string>? MeshLabels { get; init; }

    /// <summary>Retexture sections appended after the pooled emission. See
    /// <see cref="RetexEntry"/>.</summary>
    public IReadOnlyList<RetexEntry>? Retextures { get; init; }

    /// <summary>Draw-scoped retexture sections appended after the pooled emission. See
    /// <see cref="ScopedRetexEntry"/>.</summary>
    public IReadOnlyList<ScopedRetexEntry>? ScopedRetextures { get; init; }

    /// <summary>Toon ramps picked on materials of parts this build does NOT replace. See
    /// <see cref="StockRampBind"/>.</summary>
    public IReadOnlyList<StockRampBind>? StockRamps { get; init; }

    /// <summary>Semantic material-value patches, each bound around every donor draw that folds onto its
    /// target material position, or around the game's own draw of a <see cref="StockDraws"/> entry its
    /// suffix names. See <see cref="MaterialPatchEmission"/>.</summary>
    public IReadOnlyList<MaterialPatchEmission>? MaterialPatches { get; init; }

    /// <summary>The draws of unreplaced parts that material patches apply at. See
    /// <see cref="StockDrawSite"/>.</summary>
    public IReadOnlyList<StockDrawSite>? StockDraws { get; init; }

    /// <summary>The presence latches this build's latched edits reference, one per authored outfit
    /// whose edits need one. See <see cref="WitnessLatch"/>.</summary>
    public IReadOnlyList<WitnessLatch>? Latches { get; init; }

    /// <summary>The mod's own toggle key (tier 1): every suppression, draw and texture override in the
    /// emitted ini is gated on it, so one key turns the whole mod off. Null = no key, and the mod is always
    /// on. See <see cref="ModKeys"/>.</summary>
    public string? ToggleKey { get; init; }

    /// <summary>Whether the whole-mod on/off position survives a game restart: the mod key's variable is
    /// declared <c>persist</c>, so the runtime saves it on exit and restores it at the next launch.
    /// Meaningless without <see cref="ToggleKey"/>.</summary>
    public bool PersistToggleKey { get; init; }

    /// <summary>The changes claiming each hide, by the hide's own ib hash: every claim's key positions,
    /// presence latch and twin verdict, each position one guarded <c>handling = skip</c>. A hash with no
    /// entry hides unconditionally.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HideClaim>>? HideClaims { get; init; }

    /// <summary>The keys whose two-state cycle launches at position 1, so what position 0 gates starts OFF
    /// and the first press turns it on. A key not listed launches at 0. Superseded per key by
    /// <see cref="KeyCycles"/>. See <see cref="MigotoEmitter.KeyDeclarations"/>.</summary>
    public IReadOnlyCollection<string>? KeysStartingOff { get; init; }

    /// <summary>The cycle of every key whose group is not the plain two-state one: how many positions it
    /// steps and where it launches. A key named here takes these numbers over
    /// <see cref="KeysStartingOff"/>; a key named nowhere is a two-state group launching at 0.</summary>
    public IReadOnlyList<KeyCycle>? KeyCycles { get; init; }

    /// <summary>The hider flags this build's content gates read, one per part another group's state takes
    /// off screen. See <see cref="HiddenFlag"/>.</summary>
    public IReadOnlyList<HiddenFlag>? HiddenFlags { get; init; }

    /// <summary>The content flags this build's content gates read, one per change answering more than one
    /// position of its key group. See <see cref="ShownFlag"/>.</summary>
    public IReadOnlyList<ShownFlag>? ShownFlags { get; init; }

    /// <summary>Guards for the sections whose hash also fires on a sibling mesh's draws, one per guarded
    /// hash. See <see cref="TwinGuard"/>.</summary>
    public IReadOnlyList<TwinGuard>? TwinGuards { get; init; }

    /// <summary>The external writers of the guards' sticky variables — sections that identify a sibling
    /// by drawing at all rather than by a texture bound at the guarded draw. See
    /// <see cref="TwinSighting"/>.</summary>
    public IReadOnlyList<TwinSighting>? TwinSightings { get; init; }

    /// <summary>The version of the app making this build, which the mod's ini header names. Null names the
    /// app alone.</summary>
    public string? AppVersion { get; init; }
}

/// <summary>A planned operation on one target material position. Exact pixel-program gates wrap
/// every replacement draw folded onto that position; where <see cref="Suffix"/> names a
/// <see cref="StockDrawSite"/> instead, they wrap the game's own draw of that site and
/// <see cref="Submesh"/> is 0. At each such draw one pixel pass writes the group's
/// <see cref="Writes"/> over a copy of the bound constants; texture substitutions and buffer bindings are
/// restored after the draw. SkipDraw omits only the matched pass of this material. FilterIndex is the
/// input reflection family's identifier; emitted gates use stable per-program values so overlapping
/// families agree across mods. A patch without <see cref="Writes"/> adds no pass.</summary>
public sealed record MaterialPatchEmission(
    string Suffix,
    int Submesh,
    string Key,
    int ConstantBufferSlot,
    int FilterIndex,
    IReadOnlyList<string> PixelShaderHashes,
    int ByteWidth,
    IReadOnlyList<MaterialEffectTexture>? TextureOverrides = null,
    bool SkipDraw = false,
    bool IsEffect = false,
    IReadOnlyList<MaterialPatchWrite>? Writes = null);

/// <summary>One submesh draw of a replaced mesh as the game issues it: the draw's start index and index
/// count. A multi-material mesh is drawn once per material, each draw covering one submesh's index
/// range, and those two values are what a section can match a specific material's draw on.</summary>
public readonly record struct DrawShape(int First, int Count);

/// <summary>A replaced mesh's vanilla draw shapes: one <see cref="DrawShape"/> per submesh in submesh
/// order, plus the full index count for a pass that draws the whole mesh in one call.</summary>
public sealed record DrawShapeSet(IReadOnlyList<DrawShape> Shapes, int FullCount);

/// <summary>The material position one donor draw renders under. Donor draws beyond the target's
/// material positions join the last position, and a zero-count position redirects to the last
/// drawable position because the game issues no draw for the empty one.</summary>
public static class DrawMaterialFold
{
    public static int TargetMaterialPosition(DrawShapeSet target, int donorDraw)
    {
        if (donorDraw < 0 || target.Shapes.Count == 0) return -1;
        int lastDrawable = -1;
        for (int k = 0; k < target.Shapes.Count; k++)
            if (target.Shapes[k].Count > 0) lastDrawable = k;
        if (lastDrawable < 0) return -1;
        int position = Math.Min(donorDraw, target.Shapes.Count - 1);
        return target.Shapes[position].Count == 0 ? lastDrawable : position;
    }
}

/// <summary>One replaced LOD tier of a pool part: <paramref name="Part"/> is the pool part name,
/// <paramref name="Name"/> the unique emission name, <paramref name="Suffix"/> the tier level key. The
/// anchor's tier chain pairs every part's same-suffix tier, falling back to the part's lod0 recover for
/// parts without that tier (whose lod0 capture refs stay current: buffers upload at frame start).
/// <paramref name="OpKey"/> is the source mesh's stable identity, as on <see cref="PoolPart"/>.
/// <paramref name="Shapes"/> (anchor tiers only) is the tier mesh's vanilla draw-shape set; null keeps
/// the draw in the capture section. <paramref name="SourcePart"/> and <paramref name="SourceMesh"/> retain
/// the renderer-slot and game mesh names used by <paramref name="BoneVerdicts"/>; the emitter consumes
/// those upstream verdicts but never reclassifies an off-union weighted row. <paramref name="PartDisplayNames"/>
/// maps those raw renderer slots to the place names shown by the app; direct emitter callers may omit it.
/// <paramref name="Map"/> (anchor tiers only) says which of THIS tier's material positions carries each
/// lod0 material position's region; null routes donor ranges by position, which is all a caller with no
/// prefab material data can say. <paramref name="RootChain"/> is THIS tier's own renderer's root chain, as
/// on <see cref="PoolPart.RootChain"/>.</summary>
public sealed record PoolTier(string Part, string Name, string Suffix, string DumpDir, string CaptureHash,
    string? OpKey = null, DrawShapeSet? Shapes = null, string? SourcePart = null, string? SourceMesh = null,
    IReadOnlyList<PoolDerive.TierBoneVerdict>? BoneVerdicts = null,
    IReadOnlyDictionary<string, string>? PartDisplayNames = null,
    TierMaterialMap? Map = null, IReadOnlyList<uint>? RootChain = null);

/// <summary>One image of a game-wide retexture: the replacement <paramref name="DdsFile"/> and the gate it
/// rebinds under — its own key position (tier 2; null = no key of its own) or, where the change answering
/// for it answers MORE than one position of its group, that change's content flag
/// (<paramref name="ShownBy"/>). Both null = always on.
///
/// <para>One image per CLAIM, exactly as <see cref="ScopedRetexImage"/> carries one per claiming outfit.
/// A gate is a whole-frame verdict, so images whose gates can never both be open never contend and each
/// keeps its own; <see cref="ModBuilder"/> refuses two different images that could bind together.</para>
/// </summary>
public sealed record RetexImage(string DdsFile, KeyRef? ToggleKey = null, string? ShownBy = null);

/// <summary>
/// One retextured stock texture. <paramref name="Name"/> is the section suffix (unique per entry);
/// <paramref name="Hash"/> is the STOCK texture's own 8-hex 3DMigoto resource hash;
/// <paramref name="Images"/> are the replacements, each under its own gate, in claim order — at least one.
///
/// <para>The override keys on the texture resource, not on any draw, so one section covers every pass,
/// environment and LOD tier with no slot knowledge. The reach is game-wide — any mesh sampling that
/// texture is retextured, and two mods editing the same stock texture collide by construction.</para>
/// </summary>
public sealed record RetexEntry(string Name, string Hash, IReadOnlyList<RetexImage> Images)
{
    /// <summary>The single-image entry, which is what one claim on a stock texture comes to and what every
    /// emission that predates alternate-state retextures kept saying.</summary>
    public RetexEntry(string name, string hash, string ddsFile, KeyRef? toggleKey = null,
        string? shownBy = null)
        : this(name, hash, new[] { new RetexImage(ddsFile, toggleKey, shownBy) }) { }
}

/// <summary>One mesh anchor of a draw-scoped retexture: the anchor's ib <paramref name="Hash"/>, an
/// ini-safe <paramref name="Suffix"/> naming its section, and the presence latch gating the bind
/// (<see cref="WitnessLatch.Name"/>; null = the anchor is private, no latch).</summary>
public sealed record ScopedAnchor(string Hash, string Suffix, string? Latch = null);

/// <summary>One image of a draw-scoped retexture: the replacement <paramref name="DdsFile"/>, the mesh
/// <paramref name="Anchors"/> whose draws it binds at, and its own toggle key (tier 2; null = always on) —
/// one image per claiming outfit. Two images naming ONE anchor bind in list order (both gates open = last
/// wins); a gate is a whole-frame verdict, so <see cref="ModBuilder"/> refuses two DIFFERENT images at one
/// anchor, and two carrying the same file keep their separate keys but ship one copy.</summary>
public sealed record ScopedRetexImage(string DdsFile, IReadOnlyList<ScopedAnchor> Anchors,
    KeyRef? ToggleKey = null, string? ShownBy = null);

/// <summary>
/// One DRAW-SCOPED retextured stock texture: <paramref name="StockHash"/> is tagged with a derived
/// <c>filter_index</c>; each anchor mesh section probes its <c>ps-t</c> slots for the tag at draw time and
/// rebinds the matching slot to the image whose gate is open. Reach is the anchors' draws, not the
/// texture's game-wide wearer set. The tag derives from the hash itself, so any two mods scoping one stock
/// texture agree on it.
/// <para><paramref name="Images"/>: one per claiming outfit, all under the SINGLE section this stock hash
/// owns; at least one required. <paramref name="Part"/>: the change-list label a refusal names; empty when
/// the caller has none.</para>
/// </summary>
public sealed record ScopedRetexEntry(string Name, string StockHash, IReadOnlyList<ScopedRetexImage> Images,
    string Part = "", IReadOnlyList<int>? Registers = null);

/// <summary>
/// One toon ramp bound at the draws of a part this build does NOT replace — the modder picked a ramp for
/// one of its materials and nothing else about the part changes.
///
/// <para>The bind is DRAW-SCOPED, like every other ramp bind: the section keys on the part's mesh, probes
/// the ramp's own candidate registers for the ramp tag, and puts the register back afterwards. A global
/// rebind is not available here at all — the runtime's texture hash reads too little of a ramp for two of
/// them to be told apart, so it would follow every character and material sharing that prefix.</para>
///
/// <para>One mesh draws every material of the part, each material in a draw of its own index range, so
/// <paramref name="Shape"/> is what says WHICH material is drawing: the bind runs only at the draw whose
/// first index and index count are that material's.</para>
/// </summary>
/// <param name="Name">ini-safe section suffix, unique per bind.</param>
/// <param name="IbHash">the section key of the part's mesh at this level of detail — the draws this ramp
/// applies at.</param>
/// <param name="Shape">the target material's own draw at this level of detail.</param>
/// <param name="RampHash">the target material's own ramp texture hash, tagged so the probe can find which
/// register holds it. It selects a register, never a material.</param>
/// <param name="DdsFile">the picked ramp's source path (fp16 DDS, shipped verbatim).</param>
/// <param name="Part">the change-list label a refusal over this bind names. Empty when the caller has
/// none.</param>
/// <param name="TwinVerdict">the twin guard verdict naming this part's own mesh where another mesh draws on
/// the same section key; null where none does.</param>
/// <param name="Material">the texture that tells this material's draw apart from other outfits' draws of the
/// same mesh; null where no other outfit wears the mesh, or where none of the material's textures does
/// that.</param>
public sealed record StockRampBind(string Name, string IbHash, DrawShape Shape, string RampHash,
    string DdsFile, KeyRef? ToggleKey = null, string? Latch = null, string Part = "",
    string? ShownBy = null, int? TwinVerdict = null, MaterialProbe? Material = null);

/// <summary>
/// One material's own draw, at one level of detail, of a part this build does NOT replace: where the
/// shading changes and effect disables made on that material apply. The material patches naming
/// <paramref name="Id"/> as their suffix run at this draw alone — the section keys on the mesh, and the
/// draw's first index and index count say which material it is.
/// </summary>
/// <param name="Id">ini-safe identifier, unique in the build and distinct from every replacement's
/// suffix.</param>
/// <param name="IbHash">the section key of the part's mesh at this level of detail.</param>
/// <param name="Shape">the material's own draw at this level of detail.</param>
/// <param name="Part">the change-list label a refusal names.</param>
/// <param name="ToggleKey">the key position the change applies in (tier 2); null = no key of its
/// own.</param>
/// <param name="Latch">the presence latch the change waits on; null where the mesh is the outfit's
/// own.</param>
/// <param name="ShownBy">the content flag standing in for the key position, where the change answers
/// several.</param>
/// <param name="TwinVerdict">the twin guard verdict naming this part's own mesh, as on
/// <see cref="StockRampBind"/>.</param>
/// <param name="Material">the texture that tells this material's draw apart, as on
/// <see cref="StockRampBind"/>.</param>
public sealed record StockDrawSite(string Id, string IbHash, DrawShape Shape, string Part = "",
    KeyRef? ToggleKey = null, string? Latch = null, string? ShownBy = null, int? TwinVerdict = null,
    MaterialProbe? Material = null);

/// <summary>A texture one material of an unreplaced part binds at its own draw and that no other outfit
/// wearing the same mesh wears at all. Where it is bound, the draw is this material's; where it is not, the
/// draw is another outfit's, and a change on this material stays off it. <paramref name="TagValue"/> is the
/// value the texture's tag carries, which is what the probe at the draw compares.</summary>
public sealed record MaterialProbe(string TexHash, int TagValue);

/// <summary>
/// One outfit's presence latch. A sighting of any <paramref name="WitnessIbs"/> draw records into
/// <c>$zz_seen_{Name}</c>; <c>[Present]</c> commits it into <c>$zz_gate_{Name}</c> and clears it, so a
/// latched edit tests LAST frame's verdict — constant across every draw and pass of the current frame. An
/// edit on a shared anchor thus applies exactly while the authored outfit is on screen; when two wearers
/// co-draw, both show it (only the authored outfit's own witnesses are consulted).
/// </summary>
public sealed record WitnessLatch(string Name, IReadOnlyList<string> WitnessIbs);

/// <summary>One part's hider flag: <c>$zz_hid_{Name}</c>, standing at 1 while ANY of
/// <paramref name="WhenAny"/> holds — the key positions of other groups whose states take this part off
/// screen.
///
/// <para>A flag rather than one more gate term because what suppresses the content is an OR, while an ini
/// gate nests CONJUNCTS: the or-of-hiders has to be collapsed into a single variable before a content gate
/// can test it. Recomputed at load and after every key press, so a content gate always reads the answer for
/// the positions the keys currently stand in.</para></summary>
public sealed record HiddenFlag(string Name, IReadOnlyList<KeyRef> WhenAny);

/// <summary>One change's content flag: <c>$zz_shw_{Name}</c>, standing at 1 while ANY of
/// <paramref name="WhenAny"/> holds — the positions of its own key group that answer with this change.
///
/// <para>A flag for the same reason a hider flag is one: the positions are an OR, while an ini gate nests
/// CONJUNCTS, so an or-of-positions has to be collapsed into a single variable before a content gate can
/// test it. Recomputed beside the hider flags, at load and after every press. Minted ONLY where a change
/// genuinely answers more than one position — a change with a single one gates on that position directly,
/// which is what every emission that predates this kept saying.</para></summary>
public sealed record ShownFlag(string Name, IReadOnlyList<KeyRef> WhenAny);

/// <summary>
/// What one map slot binds at one submesh's Replace draw. Default is <see cref="Inherit"/>: the slot stays
/// as the game bound it, so the anchor's real map draws on the new geometry. <see cref="Neutral"/> binds
/// the shipped flat map — needed when donor UVs differ from the anchor's, since sampling the anchor's map
/// through foreign UVs reads as garbage relief. Anything else is an encoded DDS.
/// </summary>
public readonly record struct MapSlot
{
    private MapSlot(string? file, bool neutral) { File = file; IsNeutral = neutral; }

    /// <summary>The encoded DDS to bind, or null when this slot binds no file.</summary>
    public string? File { get; }

    /// <summary>Bind the shipped flat map for this kind. Only normal and RMO have one.</summary>
    public bool IsNeutral { get; }

    /// <summary>Leave the slot alone — whatever the game bound keeps drawing.</summary>
    public static MapSlot Inherit => default;

    /// <summary>Bind the shipped flat map for this kind.</summary>
    public static MapSlot Neutral => new(null, true);

    /// <summary>Bind <paramref name="ddsFile"/>.</summary>
    public static MapSlot From(string ddsFile) => new(ddsFile, false);

    /// <summary>Nothing is bound here.</summary>
    public bool IsInherit => File is null && !IsNeutral;
}

/// <summary>One submesh's map slots at its own draw. A submesh whose every slot inherits binds nothing,
/// which is what an untouched vanilla submesh of a remolded pipeline wants.</summary>
public sealed record SubmeshMaps(MapSlot Albedo = default, MapSlot Normal = default, MapSlot Rmo = default,
    MapSlot Ramp = default, MapSlot Blend = default, IReadOnlyList<PropertyMapSlot>? Properties = null)
{
    /// <summary>No slot of this submesh binds anything.</summary>
    public bool BindsNothing => !BindsStock && Ramp.IsInherit;

    /// <summary>This submesh binds one of the three picture maps. Separate from the ramp because the two
    /// are probed over different register ranges: a submesh that binds only one of them makes the build
    /// sweep only that one's.</summary>
    public bool BindsStock => !Albedo.IsInherit || !Normal.IsInherit || !Rmo.IsInherit || !Blend.IsInherit
        || Properties?.Any(p => !p.Map.IsInherit) == true;
}

/// <summary>One ordinary texture property on one replacement output. The exact shader property is the
/// binding identity; registers are the measured candidates for that property.</summary>
public sealed record PropertyMapSlot(string ShaderProperty, MapSlot Map, IReadOnlyList<int> Registers);

/// <summary>Which map a stock texture is, for the draw's slot probe.</summary>
public enum StockMapKind { Albedo, Normal, Rmo, Ramp, Blend }

/// <summary>One stock texture of a Replace anchor: its 8-hex 3DMigoto resource hash and map kind. The
/// emitter tags it with a kind-specific <c>filter_index</c>; the draw command list probes that kind's
/// candidate <c>ps-t</c> registers for those indices to find the live slots.
///
/// <para><paramref name="Part"/> is the anchor part as the change list labels it, carried so a refusal over
/// this hash can name a row the author can find. Empty when the caller has no label.</para></summary>
public sealed record StockMapTag(string Hash, StockMapKind Kind, string Part = "");

/// <summary>One ordinary property-keyed stock texture of a replacement anchor. Property and resource
/// identity remain separate: two properties may name the same hash and still represent two bindings.</summary>
public sealed record StockPropertyTag(string Hash, string ShaderProperty, IReadOnlyList<int> Registers,
    string Part = "");

/// <summary>One probe target of a twin guard: seeing a texture with <see cref="TagValue"/> bound at
/// the guarded draw identifies the sibling numbered <see cref="Verdict"/>.</summary>
public sealed record TwinProbeTag(string TexHash, int TagValue, int Verdict);

/// <summary>A guard for a section whose hash fires on several meshes' draws. The probe writes the
/// sticky per-signature variable <see cref="Var"/> whenever a tagged texture identifies a sibling;
/// the section acts while the variable holds any of <see cref="OwnVerdicts"/> (ascending). The variable
/// is never reset per frame: passes that bind no identifying texture act on the last identification.
///
/// <para>More than one verdict is what a suppression covering SEVERAL siblings needs — one section skips
/// on one hash, so hiding two meshes that share a signature is one section admitting both.</para></summary>
public sealed record TwinGuard(string Hash, string Var, IReadOnlyList<int> OwnVerdicts,
    IReadOnlyList<TwinProbeTag> Tags);

/// <summary>One change's demand that a hidden draw be skipped. <see cref="Keys"/> is the OR-list of key
/// positions asking for it, empty when the hide holds in every state; <see cref="Latch"/> the presence
/// latch it waits on, null when it waits on none; <see cref="Verdict"/> the twin guard's verdict naming the
/// change's own mesh, null when the change skips at every draw of the hash. Several changes can claim one
/// hash, and the section skips while any one of them asks.</summary>
public sealed record HideClaim(IReadOnlyList<KeyRef> Keys, string? Latch = null, int? Verdict = null);

/// <summary>An external sighting for a sticky twin variable: whenever the section owning
/// <see cref="Hash"/> fires, <see cref="Var"/> takes <see cref="Verdict"/> — proof by a mesh the
/// signature group's meshes are worn (or not worn) with.</summary>
public sealed record TwinSighting(string Hash, string Var, int Verdict);

/// <summary>
/// Assembles a runnable pooled 3DMigoto mesh-swap mod folder — one pipeline per Replace: captures each
/// pool part's posed vb0 + draw constants, recovers each part's bone palette into that pipeline's union
/// palette, converts every row into its anchor's draw space, skins the new geometry once, draws it at the
/// anchor in every pass, and hides the other outfit meshes. Pipelines may pool the same part: it is
/// captured and conditioned once, and each unique ib hash gets exactly ONE TextureOverride section, whose
/// skip is the OR across its pipelines. The emitted text is pinned by the golden emission test.
/// </summary>
public sealed partial class MigotoEmitter
{
    /// <summary>How one Replace's replacement is posed, as the ini header tells it: from each draw's own
    /// vertices with nothing kept between draws (<c>Pose</c>), the same with the parts it takes bones from
    /// recorded at their own draws (<c>Pooled</c>), or once a frame for every copy (<c>Chain</c>).</summary>
    internal enum ReplaceRoute { Pose, Pooled, Chain }

    /// <summary>One Replace as the ini header tells it: the emission name of the part it replaces, how its
    /// replacement is posed, on the pooled route the emission names of the parts it takes bones from (each
    /// part once, its lower-detail meshes folded into it, in the order their passes run), and whether any
    /// of those parts has a pick pass of its own.</summary>
    internal sealed record ReplaceSummary(string Part, ReplaceRoute Route, IReadOnlyList<string> Sources, bool Picked);

    /// <summary>What a generated mod does, as its ini header tells whoever opens the file: how many parts it
    /// replaces the meshes of, how many original meshes it hides, how many original textures it changes,
    /// whether it changes the shading of original materials, the key that turns it on and off and the keys
    /// that switch its states; then each Replace (<see cref="Replaces"/>) and each rigid replacement's part
    /// (<see cref="Rigids"/>), and which sections of the file the header names (<see cref="SlotProbe"/>,
    /// <see cref="MaterialPasses"/>, <see cref="Retextures"/>).</summary>
    internal sealed record ModSummary(int Replaced, int Hidden, int Textures, bool Shading,
        string? ModKey, IReadOnlyList<string> StateKeys)
    {
        public IReadOnlyList<ReplaceSummary> Replaces { get; init; } = Array.Empty<ReplaceSummary>();
        public IReadOnlyList<string> Rigids { get; init; } = Array.Empty<string>();
        /// <summary>A replacement's draw list finds the slots the game bound the part's textures in.</summary>
        public bool SlotProbe { get; init; }
        /// <summary>The file holds a <c>[ShaderOverride_MaterialPass_*]</c> section.</summary>
        public bool MaterialPasses { get; init; }
        /// <summary>The file holds a <c>[TextureOverride_Retex_*]</c> section.</summary>
        public bool Retextures { get; init; }
    }

    /// <summary>The comment header every generated mod.ini opens with: the app that generated it, what the
    /// mod does, how each replacement is drawn, naming the sections that do it, what the file's other
    /// section families do, and the keys. Each line is present only where its fact applies. The body
    /// follows after one blank line, the seam <see cref="ModBuilder"/> stamps the build marker at.</summary>
    internal static string IniHeader(string? appVersion, ModSummary mod)
    {
        static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];
        var lines = new List<string>
        {
            appVersion is { Length: > 0 } version
                ? $"Generated by Doll Remolding Lab {version}." : "Generated by Doll Remolding Lab.",
        };
        var does = new List<string>();
        if (mod.Replaced > 0) does.Add(mod.Replaced == 1 ? "replaces the mesh of 1 part" : $"replaces the meshes of {mod.Replaced} parts");
        if (mod.Hidden > 0) does.Add($"hides {Count(mod.Hidden, "original mesh", "original meshes")}");
        if (mod.Textures > 0) does.Add($"changes {Count(mod.Textures, "original texture", "original textures")}");
        if (mod.Shading) does.Add("changes the shading of some original materials");
        if (does.Count > 0) lines.Add(Capital(EnglishList(does)) + ".");

        if (mod.Replaces.Any(r => r.Route != ReplaceRoute.Chain))
            lines.AddRange(new[]
            {
                "At each draw of a replaced mesh, custom shader passes read the game's posed vertices,",
                "recover that draw's bone matrices (CustomShaderGather_*, CustomShaderPosePalette_*),",
                "skin the replacement with them (CustomShaderPoseSkin_*) and draw it in place of the",
                "original (CommandListDraw_*), so each copy of the part on screen is posed from its own draw.",
            });
        // one sentence per Replace that says something of its own, each said once however many states
        // repeat it
        var told = new List<string>();
        void Tell(params string[] sentence)
        {
            string text = string.Join("\n", sentence);
            if (told.Contains(text)) return;
            told.Add(text);
            lines.AddRange(sentence);
        }
        foreach (var r in mod.Replaces.Where(r => r.Route == ReplaceRoute.Pooled && r.Sources.Count > 0))
        {
            string a = r.Part;
            string ends = r.Picked ? "" : ".";
            if (r.Sources.Count == 1)
            {
                string source = r.Sources[0];
                var sentence = new List<string>
                {
                    $"{a}'s replacement is weighted to bones of {source} as well: each draw of",
                    $"{source} records its vertices and object-to-world matrix (CustomShaderRing*_{source}),",
                    $"and {a}'s draw takes the record standing where its own bones place that part{ends}",
                };
                if (r.Picked) sentence.Add("(CustomShaderPosePick_*).");
                Tell(sentence.ToArray());
            }
            else
            {
                var sentence = new List<string>
                {
                    $"{a}'s replacement is weighted to bones of {EnglishList(r.Sources)} as well: each draw of",
                    "those parts records its vertices and object-to-world matrix (CustomShaderRing*_*),",
                    $"and {a}'s draw takes, for each, the record standing where its own bones place that part{ends}",
                };
                if (r.Picked) sentence.Add("(CustomShaderPosePick_*).");
                Tell(sentence.ToArray());
            }
        }
        foreach (var r in mod.Replaces.Where(r => r.Route == ReplaceRoute.Chain))
            Tell($"{r.Part}'s replacement is skinned once a frame from the bone matrices recovered at the last draw of each",
                "part it is weighted to (CustomShaderRecover_*, CustomShaderConvert_*, CustomShaderSkin_*), so every",
                "copy of it on screen shares that pose.");
        foreach (string part in mod.Rigids)
            Tell($"{part}'s replacement is drawn in place of the original without per-vertex",
                "posing (CommandListRigid_*).");
        if (mod.SlotProbe)
            lines.AddRange(new[]
            {
                "The if-blocks on $zz_t in CommandListDraw_* find which slot the game bound each of the",
                "part's textures in, so the replacement's textures bind to the same slots.",
            });
        if (mod.MaterialPasses)
            lines.Add("ShaderOverride_MaterialPass_* carry the shading changes, one per game material program.");
        if (mod.Retextures)
            lines.Add("TextureOverride_Retex_* bind the replacement textures by the original's hash.");

        var keys = new List<string>();
        if (ModKeys.Normalize(mod.ModKey) is { } modKey)
            keys.Add($"key {ModKeys.Display(modKey)} turns the mod on and off");
        var stateKeys = mod.StateKeys.Select(k => ModKeys.Display(k)).Where(k => k.Length > 0).ToList();
        if (stateKeys.Count > 0)
            keys.Add(stateKeys.Count == 1 ? $"key {stateKeys[0]} switches between states"
                : $"keys {EnglishList(stateKeys)} switch between states");
        if (keys.Count > 0) lines.Add(Capital(string.Join(", and ", keys)) + ".");
        return string.Concat(lines.Select(line => $"; {line}\n"));
    }

    /// <summary>Which of the section families the ini header names a mod's file holds: a replacement draw
    /// list probing the slots the game bound the part's textures in (a <c>$zz_t = ps-t</c> line in a
    /// <c>[CommandListDraw*]</c> section), a <c>[ShaderOverride_MaterialPass_*]</c> section, and a
    /// <c>[TextureOverride_Retex_*]</c> section. Read off the emitted body, so the header names only
    /// sections the file holds.</summary>
    internal static (bool SlotProbe, bool MaterialPasses, bool Retextures) IniMarkers(string body)
    {
        bool slotProbe = false, materialPasses = false, retextures = false, inDraw = false;
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith('['))
            {
                inDraw = line.StartsWith("[CommandListDraw", StringComparison.Ordinal);
                materialPasses |= line.StartsWith("[ShaderOverride_MaterialPass_", StringComparison.Ordinal);
                retextures |= line.StartsWith("[TextureOverride_Retex_", StringComparison.Ordinal);
            }
            else if (inDraw && line.StartsWith($"${VarProbe} = ps-t", StringComparison.Ordinal)) slotProbe = true;
        }
        return (slotProbe, materialPasses, retextures);
    }

    /// <summary>Each pipeline as the ini header tells it (<see cref="ReplaceSummary"/>), in emission order.</summary>
    static IReadOnlyList<ReplaceSummary> ReplaceSummaries(IReadOnlyList<PipelineEmission> pipes) =>
        pipes.Select(pipe =>
        {
            string anchor = pipe.PartMeta[pipe.AnchorIdx].Part;
            if (pipe.PoseRoute is null) return new ReplaceSummary(anchor, ReplaceRoute.Chain, Array.Empty<string>(), false);
            if (pipe.PoseRoute.Sources is not { } sources)
                return new ReplaceSummary(anchor, ReplaceRoute.Pose, Array.Empty<string>(), false);
            return new ReplaceSummary(anchor, ReplaceRoute.Pooled,
                sources.Select(s => s.Part).Distinct(StringComparer.Ordinal).ToList(), sources.Any(s => s.ByBone));
        }).ToList();

    /// <summary>The keys that switch a build's states, as its header names them: every declared key but the
    /// mod's own, then every shortcut key that sets a group's position.</summary>
    static IReadOnlyList<string> StateKeys(string? modKey, IEnumerable<string?> changeKeys, IReadOnlyList<KeyCycle>? cycles)
    {
        var declared = ModKeys.Distinct(new[] { modKey }.Concat(changeKeys));
        string? own = ModKeys.Normalize(modKey);
        return declared.Where(k => !string.Equals(k, own, StringComparison.Ordinal))
            .Concat(Jumps(declared, cycles).Keys.Where(k => !string.Equals(k, own, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The header's counts of a build's meshes, each counted as the app counts it: one per part, with
    /// a part's lower-detail meshes and its states folded into it (<paramref name="meshLabels"/> names the
    /// part a mesh hash belongs to; a hash it does not name counts on its own). <c>Replaced</c> is every part
    /// a replacement draws in place of, and <c>Hidden</c> the parts hidden outright or suppressed beside a
    /// replacement, less any part a replacement draws.</summary>
    static (int Replaced, int Hidden) MeshCounts(IReadOnlyList<PipelineEmission> pipes,
        IReadOnlyList<RigidEmission> rigids, IEnumerable<string> hideHashes,
        IReadOnlyDictionary<string, string>? meshLabels)
    {
        string Label(string hash) => meshLabels is not null && meshLabels.TryGetValue(hash, out var l) ? l : hash;
        // the parts replaced, and every mesh a replacement draws in place of (their lower-detail meshes too)
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(hideHashes.Select(Label), StringComparer.Ordinal);
        foreach (var r in rigids)
        {
            replaced.Add(Label(r.Hashes[0]));
            foreach (string h in r.Hashes) drawn.Add(Label(h));
        }
        foreach (var pipe in pipes)
        {
            string Hash(string part) => pipe.CapHashes.TryGetValue(part, out var h) ? h : $"REPLACE_{part}_ib";
            string anchor = pipe.PartMeta[pipe.AnchorIdx].Part;
            string anchorLabel = Label(Hash(anchor));
            replaced.Add(anchorLabel);
            drawn.Add(anchorLabel);
            foreach (var t in pipe.TierMeta.Where(t => t.Part == anchor)) drawn.Add(Label(t.Hash));
            foreach (var (part, _, _, _) in pipe.PartMeta)
            {
                if (part == anchor) continue;
                bool suppressed = pipe.NoSkip?.Contains(part) != true
                    || pipe.SuppressWhen?.ContainsKey(part) == true;
                if (!suppressed) continue;
                hidden.Add(Label(Hash(part)));
                foreach (var t in pipe.TierMeta.Where(t => t.Part == part)) hidden.Add(Label(t.Hash));
            }
            foreach (var claim in pipe.GroupClaims)
                if (claim.Hidden || claim.HiddenWhen is { Count: > 0 }) hidden.Add(Label(claim.Hash));
        }
        hidden.ExceptWith(drawn);
        return (replaced.Count, hidden.Count);
    }

    /// <summary>Where this build may keep solved operators (see <see cref="OperatorCachePath"/>).
    /// Null = solve fresh, write nothing.</summary>
    public string? OperatorCacheDir { get; init; }

    /// <summary>Most cores the operator solve may spread across (see <see cref="SolveOperators"/>). Null =
    /// every logical processor.</summary>
    public int? CpuLimit { get; init; }

    /// <summary>The ps registers this build probes for the anchor's stock maps and its ramp. The registers
    /// vary per shader variant and per scene, so they are read off shipped data rather than authored here;
    /// a plan naming none emits no probes and no binds.</summary>
    public ShaderSlotPlan Slots { get; init; } = ShaderSlotPlan.Shipped;

    /// <summary>The registers a draw's stock-map probe sweeps, and what it saves and restores around the
    /// picture-map binds.</summary>
    IReadOnlyList<int> ProbeSlots => Slots.StockMaps;

    /// <summary>The ps-t registers a draw saves and restores: the candidate range of each kind of bind it
    /// actually ships, merged and ascending. The two ranges overlap, and a register in both is saved
    /// once — two <c>Resource_SaveT</c> declarations on one register would be a duplicate section.</summary>
    IReadOnlyList<int> SavedSlots(bool stock, bool ramp, IEnumerable<int>? properties = null) =>
        (stock ? ProbeSlots : Enumerable.Empty<int>())
            .Concat(ramp ? Slots.Ramp : Enumerable.Empty<int>())
            .Concat(properties ?? Enumerable.Empty<int>())
            .Distinct().OrderBy(s => s).ToList();

    /// <summary>The slot variables a draw list's binds read, each with the registers its probe sweeps: a
    /// stock picture kind some submesh binds, a shader property some submesh binds, and the ramp when some
    /// submesh binds one. A kind no submesh binds is probed but never bound, so its slot is never saved.</summary>
    IReadOnlyList<(string Var, IReadOnlyList<int> Registers)> BoundSlotVars(SubmeshMaps?[] subMaps)
    {
        var bound = new List<(string, IReadOnlyList<int>)>();
        foreach (var (kind, v) in new[] { (StockMapKind.Albedo, VarAlbedoSlot), (StockMapKind.Normal, VarNormalSlot),
                     (StockMapKind.Rmo, VarRmoSlot), (StockMapKind.Blend, VarBlendSlot) })
            if (Enumerable.Range(0, subMaps.Length).Any(di => !Slot(subMaps, di, kind).IsInherit)) bound.Add((v, ProbeSlots));
        foreach (var p in PropertySlots(subMaps)) bound.Add((PropertyVar(p.ShaderProperty), p.Registers));
        if (RampTexed(subMaps)) bound.Add((VarRampSlot, Slots.Ramp));
        return bound;
    }

    /// <summary>The save of every slot a draw list's binds can touch, taken once the probe has named it:
    /// one reference per bound kind, at the register the probe answered, and none for a kind no register
    /// holds. Saving the whole sweep range unconditionally cost a reference and a rebind per register at
    /// every draw of the part, most of the draw's CPU time.</summary>
    static string TextureSaves(IReadOnlyList<(string Var, IReadOnlyList<int> Registers)> bound) =>
        string.Concat(bound.SelectMany(b => b.Registers.Select(s => $"if ${b.Var} == {s}\nResource_SaveT{s} = ref ps-t{s}\nendif\n")));

    /// <summary>The game's own bind put back at every slot <see cref="TextureSaves"/> took, after the last draw.</summary>
    static string TextureRestores(IReadOnlyList<(string Var, IReadOnlyList<int> Registers)> bound) =>
        string.Concat(bound.SelectMany(b => b.Registers.Select(s => $"if ${b.Var} == {s}\nps-t{s} = Resource_SaveT{s}\nendif\n")));

    // filter_index values for the slot tags — distinctive on purpose: a texture's probe answer is the
    // HIGHEST-priority filter_index among every ini's sections on that hash, so a third-party mod tagging
    // the same stock texture with a common small value would be indistinguishable from ours.
    internal const int FilterAlbedo = 3301, FilterNormal = 3302, FilterRmo = 3303, FilterRamp = 3304,
        FilterBlend = 3305;

    /// <summary>The draw-scoped retexture tag for a stock texture, derived from the hash so every mod tags
    /// one texture with the SAME value (disagreement would silently break the loser's slot detection).
    /// Range [1e6, 16e6): float32-exact for the ini's float compare, clear of the kind tags above and of
    /// small third-party values.</summary>
    internal static int RetexTag(string stockHash) =>
        1_000_000 + (int)(Convert.ToUInt32(stockHash, 16) % 15_000_000);

    /// <summary>The probe tag value for each stock base color a twin guard identifies a sibling by: the
    /// albedo kind value where the build's own slot tags carry it, else the value derived from the hash.
    /// This owns the assignment the emitted tag sections follow — slot-tag dedupe is first-kind-wins,
    /// and a scoped retexture takes its hash back from a slot tag, so that hash derives again.</summary>
    public static Func<string, int> TwinTagValues(IEnumerable<StockMapTag> slotTags,
        IEnumerable<string> scopedStockHashes)
    {
        var scoped = scopedStockHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, StockMapKind>(StringComparer.OrdinalIgnoreCase);
        var slotTaggedAlbedos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in slotTags)
            if (kinds.TryAdd(t.Hash, t.Kind) && t.Kind == StockMapKind.Albedo && !scoped.Contains(t.Hash))
                slotTaggedAlbedos.Add(t.Hash);
        return hash => slotTaggedAlbedos.Contains(hash) ? FilterAlbedo : RetexTag(hash);
    }

    /// <summary>The two variables of a presence latch (see <see cref="WitnessLatch"/>).</summary>
    static string GateVar(string latch) => $"zz_gate_{latch}";
    static string SeenVar(string latch) => $"zz_seen_{latch}";

    /// <summary>The sticky per-signature variable of a twin guard (see <see cref="TwinGuard"/>). The one
    /// namer of the <c>zz_tw_*</c> space, so a guard and the sightings that write its variable agree by
    /// construction.</summary>
    public static string TwinVar(string signatureKey) => $"zz_tw_{signatureKey}";

    // The draw's own variables: the slot each probe answer landed in, and the scratch the probe reads
    // through. 3DMigoto namespaces named variables per ini file, so two of these mods never collide.
    const string VarProbe = "zz_t", VarAlbedoSlot = "zz_slot_a", VarNormalSlot = "zz_slot_n",
        VarRmoSlot = "zz_slot_r", VarRampSlot = "zz_slot_rm", VarBlendSlot = "zz_slot_b";

    // The scoped-retexture sections' own probe scratch + found-slot variable, separate from the draw
    // list's so a scoped bind can never clobber a Replace draw's probe state mid-frame.
    const string VarRetexProbe = "zz_rt", VarRetexSlot = "zz_rslot";

    // A stock ramp bind's own probe scratch, separate from both of the above for the same reason: these
    // sections fire at draws of parts nothing else in the build touches, and must not carry state into or
    // out of one that does. The register the ramp was found in is the draw list's own VarRampSlot — one
    // name for one question, wherever it is asked.
    const string VarStockRampProbe = "zz_sr";

    /// <summary>The section-local flag saying this section put something in <c>ps-t</c><paramref
    /// name="register"/> at the current draw, so its restore after the draw runs only then. A section that
    /// bound nothing leaves the register to whoever did: its own save may hold another mod's resource, and
    /// putting that back would outlast the draw.</summary>
    static string BoundVar(int register) => $"zz_bt{register}";

    /// <summary>The section-local flag saying this section bound its patched constant buffer for a material
    /// patch group at the current draw.</summary>
    static string BoundCbVar(string gid) => $"zz_bcb_{gid}";

    // The section-local scratch a material probe reads each register through, and its answer: whether the
    // material's own texture is bound at this draw.
    const string VarMaterialProbe = "zz_mt", VarMaterialSeen = "zz_mat";

    // The scratch a multi-verdict twin guard folds its verdicts into: the ini nests if/endif rather than
    // offering an OR, so the admitted verdicts each set this and the body opens on it once. Declared only
    // in builds that carry such a guard.
    const string VarTwinOk = "zz_twok";

    /// <summary>The ini section of a pipeline's convert chunk <paramref name="chunk"/>. Chunk 0 keeps the
    /// name the single convert has always had; a later chunk's name puts its number before the suffix, as
    /// the witness convert's does, so it cannot equal another pipeline's section whatever that suffix
    /// is.</summary>
    static string ConvertSection(string sfx, int chunk) =>
        chunk == 0 ? $"CustomShaderConvert_{sfx}" : $"CustomShaderConvertC{chunk}_{sfx}";

    /// <summary>The shader file of a pipeline's convert chunk <paramref name="chunk"/>, named on the same
    /// rule as <see cref="ConvertSection"/>.</summary>
    static string ConvertFile(string sfx, int chunk) =>
        chunk == 0 ? $"convert_cs_{sfx}.hlsl" : $"convert_c{chunk}_cs_{sfx}.hlsl";

    /// <summary>The slot-tag filter value carried for a stock map kind.</summary>
    static int KindFilter(StockMapKind kind) => kind switch
    {
        StockMapKind.Albedo => FilterAlbedo,
        StockMapKind.Normal => FilterNormal,
        StockMapKind.Ramp => FilterRamp,
        StockMapKind.Blend => FilterBlend,
        _ => FilterRmo,
    };

    /// <summary>The shipped flat map for a kind, or null for a kind that has none.</summary>
    static string? NeutralResource(StockMapKind kind) => kind switch
    {
        StockMapKind.Normal => "Resource_NeutralN",
        StockMapKind.Rmo => "Resource_NeutralRMO",
        _ => null,
    };

    /// <summary>A replacement's slot for one submesh's map kind, inherit when the submesh has no row.</summary>
    static MapSlot Slot(SubmeshMaps?[] subMaps, int draw, StockMapKind kind) => subMaps[draw] is not { } m
        ? MapSlot.Inherit
        : kind switch
        {
            StockMapKind.Albedo => m.Albedo,
            StockMapKind.Normal => m.Normal,
            StockMapKind.Ramp => m.Ramp,
            StockMapKind.Blend => m.Blend,
            _ => m.Rmo,
        };

    static string PropertyVar(string shaderProperty)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(shaderProperty)) hash = (hash ^ b) * 16777619;
        return $"zz_slot_x{hash:x8}";
    }

    static IReadOnlyList<PropertyMapSlot> PropertySlots(SubmeshMaps?[] subMaps) => subMaps
        .Where(m => m?.Properties is not null)
        .SelectMany(m => m!.Properties!)
        .Where(p => !p.Map.IsInherit)
        .GroupBy(p => p.ShaderProperty, StringComparer.Ordinal)
        .Select(g => new PropertyMapSlot(g.Key, MapSlot.Inherit,
            g.SelectMany(p => p.Registers).Distinct().OrderBy(x => x).ToList()))
        .OrderBy(p => p.ShaderProperty, StringComparer.Ordinal).ToList();

    /// <summary>Any draw of this replacement binds a fixed picture map, so the draw list needs the stock-kind
    /// probe and its ps-t save/restore. Exact property pictures have their own probe and stand alone.</summary>
    static bool DonorTexed(SubmeshMaps?[] subMaps) => subMaps.Any(m => m is not null
        && (!m.Albedo.IsInherit || !m.Normal.IsInherit || !m.Rmo.IsInherit || !m.Blend.IsInherit));

    /// <summary>Any draw of this replacement binds a ramp. Asked apart from <see cref="DonorTexed"/> so a
    /// build that ships no ramp emits no ramp probe, save or restore at all.</summary>
    static bool RampTexed(SubmeshMaps?[] subMaps) => subMaps.Any(m => m is not null && !m.Ramp.IsInherit);

    /// <summary>Any draw of this replacement asks for the shipped flat map of <paramref name="kind"/>.</summary>
    static bool UsesNeutral(SubmeshMaps?[] subMaps, StockMapKind kind) =>
        subMaps.Any(m => m is not null && kind switch
        {
            StockMapKind.Normal => m.Normal.IsNeutral,
            StockMapKind.Rmo => m.Rmo.IsNeutral,
            _ => false,
        });

    /// <summary>One conjunct of a gate: an ini variable and the value it must hold. A key variable is
    /// tested against its state index in the cycle; a presence latch and a hider flag are ordinary
    /// variables tested against 1 and 0.</summary>
    readonly record struct GateVarState(string Var, int State)
    {
        public string Line => $"if ${Var} == {State}";
    }

    /// <summary>The conditions a block is gated on: the mod's tier-1 key, then the change's tier-2 key at
    /// its own position in that key's cycle, via <see cref="ModKeys.VariableFor"/>, emitted as nested
    /// <c>if $v == N</c> blocks. An EMPTY gate emits nothing at all: an unkeyed mod's ini is byte-identical
    /// to the emission that predates keys. Two changes bound to one key share one variable and step
    /// together (the build warns).</summary>
    readonly struct Gate
    {
        public readonly GateVarState[] Terms;

        public Gate(params KeyRef?[] keys) : this(keys, null) { }

        /// <summary><paramref name="rawTerms"/> are pre-made variable tests (a presence latch's gate
        /// variable at 1, a hider flag at 0), appended after the key terms — the same <c>if $v == N</c>
        /// substrate.</summary>
        public Gate(IEnumerable<KeyRef?> keys, IEnumerable<GateVarState>? rawTerms)
        {
            var terms = new List<GateVarState>();
            void Add(GateVarState term)
            {
                if (!terms.Contains(term)) terms.Add(term);
            }
            foreach (var k in keys)
                if (ModKeys.NormalizeRef(k) is { } n)
                    Add(new GateVarState(ModKeys.VariableFor(n.Key), n.State));
            foreach (var t in rawTerms ?? Array.Empty<GateVarState>())
                if (!string.IsNullOrEmpty(t.Var)) Add(t);
            Terms = terms.ToArray();
        }

        /// <summary>Nothing gates this block — emit it bare.</summary>
        public bool IsAlwaysOn => Terms.Length == 0;

        /// <summary>The gate's identity, for deduping two contributions that gate identically.</summary>
        public string Id => string.Join('|', Terms.Select(t => $"{t.Var}={t.State}"));

        public void Open(StringBuilder p) { foreach (var t in Terms) p.Append(t.Line).Append('\n'); }
        public void Close(StringBuilder p) { for (int i = 0; i < Terms.Length; i++) p.Append("endif\n"); }

        /// <summary>The gate's lines wrapped around <paramref name="body"/>, as a list of ini lines — for
        /// the capture units, which collect lines rather than write straight to a builder.</summary>
        public IEnumerable<string> Wrap(IEnumerable<string> body)
        {
            foreach (var t in Terms) yield return t.Line;
            foreach (var line in body) yield return line;
            for (int i = 0; i < Terms.Length; i++) yield return "endif";
        }
    }

    /// <summary>A presence latch's gate variable as a gate term: latches are on/off, so the test is against
    /// 1 exactly as it was before keys became ordinal.</summary>
    static GateVarState[]? LatchTerms(string? latch) =>
        latch is null ? null : new[] { new GateVarState(GateVar(latch), 1) };

    /// <summary>A hider flag as a gate term. Content draws while the flag reads 0 — an ordinal test like
    /// every other, so nothing in the substrate has to express a negation.</summary>
    static GateVarState? HiddenTerm(string? flag) =>
        flag is null ? null : new GateVarState(HiddenVar(flag), 0);

    /// <summary>A content flag as a gate term: the change draws while the flag reads 1, which is while any
    /// of the positions answering with it holds.</summary>
    static GateVarState? ShownTerm(string? flag) =>
        flag is null ? null : new GateVarState(ShownVar(flag), 1);

    /// <summary>A content flag as a whole raw-term list, for a gate that carries no others.</summary>
    static IEnumerable<GateVarState>? ShownTerms(string? flag) =>
        ShownTerm(flag) is { } term ? new[] { term } : null;

    /// <summary>The same suppression said once, where a set of guarded skips provably covers EVERY
    /// position of one key group. Three skips reading <c>f7 == 0</c>, <c>1</c> and <c>2</c> on a
    /// three-position key say exactly what one bare skip says, and the bare one is the honest reading:
    /// nothing about that key decides this draw.
    ///
    /// <para>All or nothing, and only where the gates are otherwise identical — a set differing in more
    /// than the one position states a condition that survives the collapse. Only a key whose cycle this
    /// build DECLARES counts: without one the emitter does not know how many positions the group has, so
    /// it cannot know the set covers them. The mod's own key is never collapsed; mod-off has to return the
    /// vanilla draw, and its term only ever names position 0 anyway.</para></summary>
    static List<Gate> CollapseSkips(List<Gate> gates, string? modKey, IReadOnlyList<KeyCycle>? cycles)
    {
        if (gates.Count < 2 || cycles is not { Count: > 0 }) return gates;
        string? modVar = modKey is null ? null : ModKeys.VariableFor(modKey);
        var widths = cycles.Where(cycle => ModKeys.Normalize(cycle.Key) is not null)
            .GroupBy(cycle => ModKeys.VariableFor(cycle.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().StateCount, StringComparer.Ordinal);
        foreach (var (variable, width) in widths)
        {
            if (string.Equals(variable, modVar, StringComparison.Ordinal) || width < 2) continue;
            var states = new HashSet<int>();
            var rest = new List<GateVarState>();
            bool uniform = true;
            foreach (var gate in gates)
            {
                var mine = gate.Terms.Where(term =>
                    string.Equals(term.Var, variable, StringComparison.Ordinal)).ToList();
                var others = gate.Terms.Where(term =>
                    !string.Equals(term.Var, variable, StringComparison.Ordinal)).ToList();
                if (mine.Count != 1 || (states.Count > 0 && !others.SequenceEqual(rest)))
                {
                    uniform = false;
                    break;
                }
                if (states.Count == 0) rest = others;
                states.Add(mine[0].State);
            }
            if (!uniform || states.Count != width
                || !Enumerable.Range(0, width).All(states.Contains)) continue;
            return new List<Gate> { new(Array.Empty<KeyRef?>(), rest) };
        }
        return gates;
    }

    /// <summary>One guarded skip per extra hiding state, each carrying the mod key and the part's own
    /// presence latch. Empty where the content gate alone decides what the vanilla draw does.</summary>
    static List<Gate> SuppressGates(IReadOnlyList<KeyRef>? when, string? modKey,
        IEnumerable<GateVarState>? rawTerms) =>
        (when ?? Array.Empty<KeyRef>())
            .Select(term => new Gate(new KeyRef?[] { ModTerm(modKey), term }, rawTerms)).ToList();

    /// <summary>The raw gate terms plus one more, either of which may be absent.</summary>
    static IEnumerable<GateVarState>? With(IEnumerable<GateVarState>? terms, GateVarState? extra) =>
        extra is not { } one ? terms
            : (terms ?? Array.Empty<GateVarState>()).Append(one);

    sealed record MergedTierWarning(string AffectedPart, string Tier,
        IReadOnlyList<string> OwningParts, IReadOnlyList<(uint Hash, string? Name)> Bones,
        IReadOnlyDictionary<string, string>? DisplayNames = null);

    static string EnglishList(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}",
    };

    static string FormatMergedTierWarning(MergedTierWarning issue)
    {
        string Display(string raw) => issue.DisplayNames is not null
            && issue.DisplayNames.TryGetValue(raw, out var supplied) ? supplied : raw;
        var owners = issue.OwningParts.Select(p => $"'{Display(p)}'").ToList();
        string namedOwners = owners.Count <= 2
            ? EnglishList(owners)
            : $"{owners[0]}, {owners[1]}, and {owners.Count - 2} more "
                + $"part{(owners.Count - 2 == 1 ? "" : "s")}";
        return $"'{Display(issue.AffectedPart)}' does not show some geometry from "
            + $"{namedOwners} at longer view distances. The build log names the bones.";
    }

    static string FormatMergedTierDiagnostic(MergedTierWarning issue)
    {
        var owners = issue.OwningParts.Select(p => $"'{p}'").ToList();
        var bones = issue.Bones.OrderBy(b => b.Hash).Select(b => b.Name is { } name
            ? $"'{name}' (0x{b.Hash:x8})"
            : $"no matching chain suffix (0x{b.Hash:x8})").ToList();
        return $"MERGED tier geometry: affected part '{issue.AffectedPart}'; tier mesh '{issue.Tier}'; "
            + $"owning parts {EnglishList(owners)}; bones {EnglishList(bones)}.";
    }

    /// <summary><paramref name="UnionBones"/>/<paramref name="VertexCount"/> are totals across pipelines.
    /// <paramref name="Warnings"/> are user-facing and actionable; <paramref name="Diagnostics"/> record
    /// what the emission did, reaching the build log and no UI surface.
    ///
    /// <para><paramref name="Palette"/> maps a pipeline's suffix to the palette geography its shipped skin
    /// indices are stated in. Both halves are the emission's own decisions, so nothing else can restate
    /// them.</para></summary>
    public readonly record struct Result(string OutDir, int UnionBones, int VertexCount,
        IReadOnlyList<string> Warnings, IReadOnlyList<string> Diagnostics,
        IReadOnlyDictionary<string, PipelinePalette>? Palette = null);

    /// <summary>Where one pipeline's shipped blend indices land. <paramref name="UnionBones"/> is the union
    /// this emission built from the DUMPS — the count a compiled donor's indices must have been stated
    /// against — and <paramref name="GroupBase"/> the first slot of the appended coverage-group region,
    /// past the union and past the witness reservations (see <see cref="RemapSkinIndices"/>). GroupBase is
    /// meaningless where the pipeline carries no group.</summary>
    public readonly record struct PipelinePalette(int CompiledUnionBones,
        IReadOnlyList<int> UnionSourceRows, uint GroupBase, IReadOnlyList<int> GroupSourceRows)
    {
        public int UnionBones => UnionSourceRows.Count;
    }

    /// <summary>The pruning decisions for one pipeline. Source-row demands are local-bone indices keyed by
    /// the physical recovery source; the build-wide union of these dictionaries defines shared operators.</summary>
    sealed class PalettePrunePlan
    {
        public required PoolMath.UnionResult FullUnion;
        public required HashSet<int> SkinUnionRows;
        public required HashSet<int> RetainedUnionRows;
        public required HashSet<int> UsedGroupRows;
        public required Dictionary<(string Name, string Dir), HashSet<int>> SourceRows;
        public required HashSet<(string Name, string Dir)> PoolSources;
        public required HashSet<(string Name, string Dir)> TierSources;
        public required HashSet<(string Name, string Dir)> GroupSources;
        /// <summary>The pooled pose route's verdict for this pipeline: null where no part but the anchor
        /// supplies rows (the self-contained route decides) or the pipeline is the identity build.</summary>
        public PooledPlan? Pooled;
        /// <summary>Build-log lines about witness rows retained for the chain's conversion: withdrawn where
        /// the pipeline takes the pose route, which converts nothing through witnesses.</summary>
        public readonly List<string> WitnessLines = new();
        /// <summary>Build-log lines about rows retained to place a part on the pooled route: withdrawn where
        /// the pipeline keeps the chain after all.</summary>
        public readonly List<string> PlacementLines = new();
    }

    /// <summary>How a part a pooled Replace takes rows from is matched to each copy of the replaced part:
    /// its root is the anchor's own (<c>SameRoot</c>: the copy's draw binds the anchor's matrix), its root's
    /// nearest recovered ancestor is the anchor's (<c>ByAnchorBone</c>) or another such part's
    /// (<c>BySourceBone</c>) and carries the root's rest origin to where the copy's root stands, or nobody
    /// recovers an ancestor and the copy's root is looked for near the anchor's own position
    /// (<c>ByAnchorPosition</c>).</summary>
    enum Placement { SameRoot, ByAnchorBone, BySourceBone, ByAnchorPosition }

    /// <summary>One source mesh's placement: its emission name and dump, its pool part, whether it is a
    /// lower-detail mesh, its renderer's root bone, how it is placed, the bone that places it and, for
    /// <see cref="Placement.BySourceBone"/>, the pool part that recovers that bone.</summary>
    sealed record SourcePlacement(string Mesh, string Dir, int Part, bool IsTier, uint Root, Placement Kind,
        uint Bone, int Placer);

    /// <summary>A pooled pipeline's placement verdicts, every source mesh in the order its passes run, or
    /// the reason the pipeline keeps the once-per-frame chain.</summary>
    sealed class PooledPlan
    {
        public string? Refusal;
        public readonly List<SourcePlacement> Sources = new();
    }

    sealed record PipelineEmission(string Sfx,
        List<(string Part, int N, int Nb, int Rows)> PartMeta, int AnchorIdx,
        IReadOnlyDictionary<string, string> CapHashes, int Ub, int Vcount, int Vb1Stride, string IbFmt,
        List<(int Count, int Start, int Base)> Draws, SubmeshMaps?[] SubMaps,
        HashSet<string>? NoSkip,
        List<(string Part, string Name, string Suffix, string Hash, int Rows, DrawShapeSet? Shapes,
            TierMaterialMap? Map)> TierMeta,
        bool Lod0WitnessConvert, KeyRef? ToggleKey, string? Latch, bool HideWhenOff, string? HiddenBy,
        string? ShownBy,
        IReadOnlyDictionary<string, IReadOnlyList<KeyRef>>? SuppressWhen,
        List<GroupMemberEmission> GroupMembers,
        List<GroupMemberClaim> GroupClaims, List<(string Part, int Pairs)> Ties,
        IReadOnlyDictionary<string, int> TierTies,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? PresenceHashes, DrawShapeSet? AnchorShapes,
        IReadOnlyList<StreamVariant> Vb1Variants, IReadOnlyDictionary<string, int> TierVb1,
        HashSet<string> ConvertedOps, PoseRouteEmission? PoseRoute = null)
    {
        /// <summary>The operator resource this pipeline's recover of <paramref name="mesh"/> binds: its own
        /// converted copy where the mesh binds bones elsewhere than this pipeline's reference
        /// (<see cref="ConvertedOps"/>), else the solved operator every pipeline shares.</summary>
        public string CpinvResource(string mesh) =>
            ConvertedOps.Contains(mesh) ? $"Resource_{mesh}_Cpinv_{Sfx}" : $"Resource_{mesh}_Cpinv";
    }

    /// <summary>The per-copy pose route a pipeline runs instead of the once-per-frame chain when every
    /// row it recovers comes from the replaced part itself: at every draw of the part, pixel passes rebuild
    /// the posed stream from that draw's own packet (a palette pass per kernel mesh, a skin pass per piece)
    /// and the draw reads it directly, so each copy on screen draws from its own pose with nothing kept
    /// between draws. <c>Kernels</c>: one per mesh the anchor is captured from (its lod0 and each tier of
    /// its own that recovers), with the size of the packet its gather fills and whether its operator is
    /// slim; <c>TierKernel</c>: which mesh's palette pass each anchor tier's draw runs; <c>PaletteRows</c>:
    /// the palette texture's width (four per palette slot); <c>Pieces</c>: the donor's draw ranges cut
    /// into stream-sized pieces, in draw order; <c>ViewWidth</c>: the widest piece's stream in elements,
    /// the viewport every skin pass borrows. <c>Sources</c>: on the pooled route, every mesh of the parts the
    /// replacement takes rows from, in the order their passes run; null on the self-contained route.</summary>
    sealed record PoseRouteEmission(IReadOnlyList<(string Mesh, int Tier, int Packet, bool Slim)> Kernels,
        IReadOnlyDictionary<string, string> TierKernel, int PaletteRows, IReadOnlyList<PosePiece> Pieces, int ViewWidth,
        IReadOnlyList<PoseSource>? Sources = null);

    /// <summary>One source mesh of the pooled route: its emission name, the emission name of the part it
    /// belongs to, the entries of its packet, whether its operator is slim, and whether a bone's row places
    /// it, in which case a pick pass reading the palette so far and the row mask chooses its copy ahead of
    /// its palette pass; any other source's palette pass chooses its copy itself.</summary>
    sealed record PoseSource(string Mesh, string Part, int Packet, bool Slim, bool ByBone);

    /// <summary>Ring slots kept per source mesh on the pooled route, filled in turn, one per draw of the mesh:
    /// a copy drawn in several passes fills several slots with the same rows, which the pick counts as one,
    /// and an anchor draw reads only those written in its own frame. Sixteen keep the last sixteen draws of
    /// the mesh, every copy of the frame in the two-copy captures the route was sized on (about six draws a
    /// copy a frame, two a pass).</summary>
    // Accepted consequence: a copy whose last draw of the mesh is followed by sixteen draws of it by other
    // copies before the copy's replacement draw has lost its slots, and its replacement finds no copy or a
    // neighbour's. Where the game draws one material of every copy together, as the captures show, that takes
    // nine copies of a two-material source mesh on screen together; choosing the slot per copy instead puts a
    // wait on every write, and no mod is expected to reach the limit.
    internal const int PoseRingEntries = 16;

    /// <summary>The most rows a texture the mod declares may have (Direct3D 11's limit), and so the most
    /// packet entries a source mesh's ring of <see cref="PoseRingEntries"/> slots holds: 65,472, read from the
    /// source mesh's vertices.</summary>
    internal const int MaxTextureRows = 16384;
    internal const int MaxRingPacket = (MaxTextureRows / PoseRingEntries - 1) * ComputeTemplates.PacketWidth;

    /// <summary>Refuse the build where a source mesh's ring would pass <see cref="MaxTextureRows"/>: the
    /// loaders would fail to create it, and the replacement would draw with no pose.</summary>
    internal static void RequireRingFits(string anchor, string source, int packet)
    {
        if (packet > MaxRingPacket)
            throw new AuthoredRefusalException($"'{anchor}' can't be replaced: it moves with '{source}', which "
                + "has too many vertices to follow. Remove this mesh edit");
    }

    /// <summary>The pick's margins: a slot placed by a bone's row may stand at most
    /// <see cref="PlacementCap"/> from where that row puts the root, one placed by the anchor's own position
    /// at most <see cref="PositionCap"/> (a body length); the nearest must be <see cref="PlacementAhead"/>
    /// times nearer than any other; a same-root slot matches the anchor's rows within
    /// <see cref="SameRootTolerance"/> per element. Two slots written this frame whose rows agree within
    /// <see cref="SameDrawTolerance"/> per element are one copy drawn in two passes, and the pick counts
    /// them once.</summary>
    internal const float PlacementCap = 0.30f, PositionCap = 1.2f, PlacementAhead = 3.0f,
        SameRootTolerance = 1e-4f, SameDrawTolerance = 1e-5f;

    /// <summary>The frame number wraps back to 1 here, below the float's exact-integer range.</summary>
    const int FrameWrap = 8388608;

    /// <summary>The texture holding the pooled route's frame number, which every ring slot and replaced-part
    /// capture is stamped with, and the one the <c>[Present]</c> pass writes the next number into before it
    /// is copied back. Cleared to frame 1 when the mod loads, so a ring slot no draw has written (frame 0)
    /// never matches it.</summary>
    const string FrameResource = "Resource_PoseFrame", FrameNextResource = "Resource_PoseFrameNext";

    static string PosePickFile(string mesh, string sfx) => $"pose_pick_{mesh}_{sfx}.hlsl";
    static string RingGatherFile(string mesh) => $"ring_gather_{mesh}.hlsl";
    static string RingStampFile(string mesh) => $"ring_stamp_{mesh}.hlsl";
    /// <summary>The file holding ring slot <paramref name="k"/>'s number, one per slot per mod: a ring block
    /// binds the one its write fills (see <see cref="RingSections"/>).</summary>
    static string RingSlotFile(int k) => $"ring_slot_{k}.buf";
    static string RingSlotResource(int k) => $"Resource_RingSlot_{k}";
    internal const string PoseCaptureVsFile = "pose_capture_vs.hlsl";
    internal const string PoseCapturePsFile = "pose_capture_ps.hlsl";
    internal const string PoseFrameFile = "pose_frame_ps.hlsl";

    /// <summary>One piece of the donor on the pose route: its global number (the resources' suffix), the
    /// draw range it belongs to, and its vertex, index and stream-element counts.</summary>
    sealed record PosePiece(int Number, int Range, int Vertices, int Indices, int Elements);

    /// <summary>Vertices one piece of the pose route carries: a buffer render-target view spans 16,384
    /// elements of 16 bytes, 6,553 vertices at the stream's 40-byte stride. An instance property so a
    /// test can force a small donor into several pieces.</summary>
    internal int PoseWindowVertices { get; init; } = DonorPieces.WindowVertices;

    /// <summary>The pose route's fullscreen vertex shader file, one per mod, drawn by every pose pass.</summary>
    internal const string PoseFullscreenFile = "pose_fullscreen.hlsl";

    /// <summary>The gather's pixel shader file, one per mod, shared by every mesh's gather.</summary>
    internal const string GatherPixelFile = "gather_ps.hlsl";

    /// <summary>A bone as a build-log line names it: by its name in quotes, or by its path from the skeleton's
    /// top where another bone of <paramref name="paths"/> shares the name, and by its hash where no path is
    /// known. One bone listed under several starting links (one path the tail of another) is one bone, not a
    /// shared name.</summary>
    internal static string BoneName(IReadOnlyDictionary<uint, string>? paths, uint hash)
    {
        if (paths is null || !paths.TryGetValue(hash, out var path) || path.Length == 0) return $"0x{hash:x8}";
        static string Leaf(string p) => p[(p.LastIndexOf('/') + 1)..];
        static bool SameBone(string a, string b) => string.Equals(a, b, StringComparison.Ordinal)
            || a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal);
        string leaf = Leaf(path);
        bool shared = paths.Any(kv => kv.Key != hash && string.Equals(Leaf(kv.Value), leaf, StringComparison.Ordinal)
            && !SameBone(kv.Value, path));
        return $"'{(shared ? path : leaf)}'";
    }

    /// <summary>Every bone path a build's pipelines know, as one table: a bone's hash is its path, so the
    /// pipelines' tables agree wherever they overlap. Null when no pipeline carries paths.</summary>
    static IReadOnlyDictionary<uint, string>? BonePathsOf(PoolBuildRequest req)
    {
        var all = new Dictionary<uint, string>();
        foreach (var pipe in req.Pipelines)
            foreach (var kv in pipe.BonePaths ?? new Dictionary<uint, string>())
                all.TryAdd(kv.Key, kv.Value);
        return all.Count > 0 ? all : null;
    }

    /// <summary>The donor's stream 1 re-encoded for an anchor tier whose mesh stores that stream
    /// differently from the lod0. The donor draw at a tier is read through the TIER mesh's input layout,
    /// so the lod0-shaped stream is only correct there when the two layouts agree; each layout that
    /// disagrees ships one of these. <see cref="PipelineEmission.TierVb1"/> maps a tier's emission name to
    /// its 1-based variant, and a tier absent from it binds the primary stream.</summary>
    sealed record StreamVariant(int Stride, string File);

    /// <summary>One draw a donor stream is bound at besides the one it was built for: the key its
    /// selector is looked up by, the mesh and part names a message names it by, and that mesh's channel
    /// table — null when the caller recorded none.</summary>
    sealed record TierLayout(string Key, string Mesh, string? Part, IReadOnlyList<UnityMesh.ChannelDef>? Channels)
    {
        public string Label => Part is null ? $"LOD '{Mesh}'" : $"LOD '{Mesh}' of '{Part}'";
    }

    /// <summary>The re-encoded copies of one shipped donor stream that its other draws need. The primary,
    /// <c>{fileStem}.buf</c> in <paramref name="outDir"/>, is sliced in <paramref name="builtFor"/>; every
    /// tier whose table stores <paramref name="stream"/> differently gets a copy in its own layout, shared
    /// between tiers that agree, written as <c>{fileStem}_v{k}.buf</c>. Returns the copies in k order and
    /// each such tier's 1-based k; a tier absent from the map binds the primary.
    ///
    /// <para>A tier storing nothing in the stream is left alone: its draw reads nothing from that slot. A
    /// tier storing a UV set the donor lacks gets it filled from the donor's first one, and the build log
    /// names it; a tier storing any other channel the donor lacks refuses the build, and so does any
    /// difference when
    /// <paramref name="reencode"/> is false — the caller ships that stream in one fixed shape. A channel
    /// table missing on either side is a caller error and throws: without both there is no way to tell
    /// whether the primary is readable at that tier.</para></summary>
    static (List<StreamVariant> Variants, Dictionary<string, int> ByTier) StreamVariants(string outDir,
        string fileStem, int vcount, IReadOnlyList<UnityMesh.ChannelDef>? builtFor, int stream,
        IEnumerable<TierLayout> tiers, List<string> diagnostics, bool reencode = true)
    {
        var variants = new List<StreamVariant>();
        var byTier = new Dictionary<string, int>(StringComparer.Ordinal);
        if (vcount <= 0) return (variants, byTier);
        var layouts = new List<IReadOnlyList<UnityMesh.ChannelDef>>();
        byte[]? primary = null;
        static bool Stores(IReadOnlyList<UnityMesh.ChannelDef> table, int stream) =>
            table.Any(c => c.Dimension != 0 && c.Stream == stream);
        foreach (var t in tiers)
        {
            if (builtFor is null || t.Channels is null)
                throw new InvalidOperationException(
                    $"{fileStem}: no vertex layout is recorded for {(builtFor is null ? "the replacement" : t.Label)}, "
                    + $"so stream {stream} cannot be checked against the layout its draw reads it through");
            if (!Stores(t.Channels, stream)) continue;
            if (UnityMesh.SameStreamLayout(builtFor, t.Channels, stream)) continue;
            if (!reencode || !Stores(builtFor, stream))
                throw new AuthoredRefusalException(
                    $"{t.Label} cannot be built because it stores vertex data the original part does "
                    + $"not. Internal detail: stream {stream} "
                    + (reencode ? "is absent from the replacement" : "is laid out differently and ships in one shape")
                    + ". Remove this mesh edit");
            int k = layouts.FindIndex(l => UnityMesh.SameStreamLayout(l, t.Channels, stream));
            if (k < 0)
            {
                primary ??= File.ReadAllBytes(Path.Combine(outDir, $"{fileStem}.buf"));
                byte[] recoded;
                IReadOnlyList<string> filled;
                try { recoded = UnityMesh.TranscodeStream(primary, vcount, builtFor, t.Channels, stream, out filled); }
                catch (FormatException ex)
                {
                    throw new AuthoredRefusalException(
                        $"{t.Label} cannot be built because it stores vertex data the original part does "
                        + $"not. Internal detail: {ex.Message}. Remove this mesh edit");
                }
                k = layouts.Count;
                layouts.Add(t.Channels);
                string file = $"{fileStem}_v{k + 1}.buf";
                File.WriteAllBytes(Path.Combine(outDir, file), recoded);
                variants.Add(new StreamVariant(recoded.Length / vcount, file));
                diagnostics.Add($"{t.Mesh}: stores stream {stream} differently from the lod0 — the "
                    + $"replacement's is re-encoded for it as {file}");
                if (filled.Count > 0)
                    diagnostics.Add($"{t.Mesh}: stores {string.Join(", ", filled)}, which the replacement "
                        + "does not have — filled from its TexCoord0");
            }
            byTier[t.Key] = k + 1;
        }
        return (variants, byTier);
    }

    /// <summary>The per-pipeline global naming the stream-1 variant the next donor draw binds: 0 is the
    /// primary stream. Set at every anchor capture of a pipeline that ships variants, so it never needs a
    /// reset.</summary>
    static string Vb1Var(string sfx) => $"zz_vb1_{sfx}";

    /// <summary>The rigid route's selector for one stream, read as <see cref="Vb1Var"/> is: every section
    /// of a replacement that ships variants of that stream writes it.</summary>
    static string RigidStreamVar(string sfx, int stream) => $"zz_rvb{stream}_{sfx}";

    /// <summary>A donor draw list's vertex and index binds. Stream 1 binds the primary buffer, then the
    /// variant the capturing tier named, each bound directly so it is created as a vertex buffer the way
    /// the primary is. A pipeline shipping no variant emits the four plain binds. A pose-route pipeline
    /// binds per piece instead (<see cref="PoseDraw"/>); here it only points each piece's stride-40 alias
    /// at the stream buffer the skin pass wrote, which the loader resolves at the bind.</summary>
    static string DonorBinds(PipelineEmission pipe)
    {
        string sfx = pipe.Sfx;
        if (pipe.PoseRoute is { } route)
            return string.Concat(route.Pieces.Select(p =>
                $"Resource_PoseVB_{sfx}_p{p.Number} = ref Resource_PoseRT_{sfx}_p{p.Number}\n"));
        string posed = $"Resource_NewPosed_{sfx}";
        string vb1 = $"Resource_NewVB1_{sfx}";
        var b = new StringBuilder($"vb0 = {posed}\nvb1 = {vb1}\n");
        for (int k = 1; k <= pipe.Vb1Variants.Count; k++)
            b.Append($"if ${Vb1Var(sfx)} == {k}\nvb1 = {vb1}_v{k}\nendif\n");
        return b.Append($"vb3 = {posed}\nib = Resource_NewIB_{sfx}\n").ToString();
    }

    /// <summary>The pose route's draw of one donor range: each of its pieces bound (the stream alias as
    /// streams 0 and 3, the piece's UV rows as stream 1 with the variant the capturing tier named, the
    /// piece's own index buffer) and drawn directly at base 0. Emitted where the chain route emits the
    /// range's one draw, inside the same texture binds.</summary>
    static string PoseDraw(PipelineEmission pipe, int range)
    {
        string sfx = pipe.Sfx;
        var b = new StringBuilder();
        foreach (var p in pipe.PoseRoute!.Pieces.Where(p => p.Range == range))
        {
            b.Append($"vb0 = Resource_PoseVB_{sfx}_p{p.Number}\nvb1 = Resource_PieceVB1_{sfx}_p{p.Number}\n");
            for (int k = 1; k <= pipe.Vb1Variants.Count; k++)
                b.Append($"if ${Vb1Var(sfx)} == {k}\nvb1 = Resource_PieceVB1_{sfx}_p{p.Number}_v{k}\nendif\n");
            b.Append($"vb3 = Resource_PoseVB_{sfx}_p{p.Number}\nib = Resource_PieceIB_{sfx}_p{p.Number}\n");
            b.Append($"drawindexed = {p.Indices}, 0, 0\n");
        }
        return b.ToString();
    }

    /// <summary>One wardrobe-group member draw the ini carries a fused section for: the emission name its
    /// resources and shader are filed under, the ib hash the capture keys on, whether it is the member's
    /// lod0, and the bones its dispatch covers. The dispatch normally runs in the ANCHOR's chain (K from
    /// witness geometry, gated on the mesh's presence latch); <paramref name="AtDraw"/> marks the fallback
    /// for a lod0 sharing no sound bone with the anchor — its dispatch stays at the member's own draw,
    /// where its constants copy and its geometry are same-frame by construction (K from constants).
    /// Suppression is NOT here: it is owed to every mesh a hidden member claimed, which this list is only a
    /// subset of (see <see cref="GroupMemberClaim"/>).</summary>
    /// <para><paramref name="PreferLod0"/> (tier meshes only): the member's lod0 emission name when it
    /// carries a fused section of its own — the tier's in-chain dispatch defers to it when both latched
    /// in one frame, so the colour draw never skins from a decimated recovery.</para>
    sealed record GroupMemberEmission(string Name, string Hash, bool Lod0, int Bones, bool AtDraw = false,
        string? PreferLod0 = null);

    /// <summary>The presence latch of one captured mesh (pool part, tier or group member): its capture
    /// section records the sighting, <c>[Present]</c> commits it, and a chain dispatch reading the gate
    /// tests LAST frame's draw stream — a verdict constant across the current frame, independent of where
    /// any draw falls in it.</summary>
    static string MeshLatch(string name) => $"src_{name}";

    /// <summary>One wardrobe-group member draw the build CLAIMED a capture hash for, whether or not the
    /// emission ended up with a fused section for it: three verdicts drop a mesh after the claim (no lod0,
    /// no witness bone, an all-sentinel map), and a HIDDEN member's suppression is owed to the mesh rather
    /// than to the dispatch. <see cref="GroupMemberEmission"/> is the subset that also draws.</summary>
    sealed record GroupMemberClaim(string Name, string Hash, bool Hidden,
        IReadOnlyList<KeyRef>? HiddenWhen);

    /// <summary>The sticky per-pipeline global an AT-DRAW member lod0 run-line waits on: set where the
    /// anchor's constant buffer is captured — its lod0 capture and nowhere else, since that is the only
    /// draw whose <c>copy vs-cb1</c> fills the resource a constants rebase reads. Never reset. In-chain
    /// member dispatches carry no such flag: the chain itself runs at the anchor's draw.</summary>
    static string GroupCbVar(string sfx) => $"zz_grp_cb_{sfx}";

    /// <summary>The in-chain member dispatches, appended after the convert (their writes land in the
    /// converted palette's appended region, which the converts carry through) and before the skin that
    /// reads them. Each is gated on its own mesh's presence latch — LAST frame's draw stream, committed in
    /// <c>[Present]</c>, so the verdict is one value for the whole frame no matter where the member's draw
    /// falls in it. An unworn variant's latch clears and its dispatch stops; the worn one's rows stand.</summary>
    static void MemberRuns(List<string> chain, PipelineEmission pipe, string sfx)
    {
        foreach (var m in pipe.GroupMembers)
        {
            if (m.AtDraw) continue;
            chain.Add($"if ${GateVar(MeshLatch(m.Name))} == 1");
            // Both of a member's meshes can latch in one frame (lod0 in the colour pass, a tier in
            // shadow); dispatched unconditionally the tier would write LAST and the colour draw would
            // skin from the decimated recovery. A tier defers to its member's live lod0.
            if (!m.Lod0 && m.PreferLod0 is { } lod0)
                chain.Add($"if ${GateVar(MeshLatch(lod0))} == 0");
            chain.Add($"run = CustomShaderGroup_{m.Name}_{sfx}");
            if (!m.Lod0 && m.PreferLod0 is not null) chain.Add("endif");
            chain.Add("endif");
        }
    }

    /// <summary>One pool mesh's recover run-line: the anchor's runs bare (the chain fires at its draw),
    /// any other part's waits on the PART's presence latch — a source the scene state never renders never
    /// runs its recover, so its owned rows stay for the tie underlay instead of a never-substantiated
    /// copy posing them with garbage. The latch is part-grained (any of its meshes' draws raise it), so
    /// a part on screen at another detail still recovers from its last captured pair — a consistent
    /// stale frame, today's off-screen class — and the tie's complement gate leaves no state unserved.</summary>
    static void RecoverRun(List<string> chain, PipelineEmission pipe, int partIdx, string meshName, string sfx)
    {
        if (pipe.PartMeta[partIdx].Rows == 0) return;
        if (partIdx == pipe.AnchorIdx)
        {
            chain.Add($"run = CustomShaderRecover_{meshName}_{sfx}");
            return;
        }
        chain.Add($"if ${GateVar(MeshLatch(pipe.PartMeta[partIdx].Part))} == 1");
        chain.Add($"run = CustomShaderRecover_{meshName}_{sfx}");
        chain.Add("endif");
    }

    /// <summary>The tie underlay's run-lines, after the members and before the skin: one per tied part,
    /// firing only while the part's latch is down — no draw of ANY of its meshes last frame, dropped
    /// tiers included. The frame it returns, its gated recover resumes and overwrites the tied rows with
    /// live articulation.</summary>
    static void TieRuns(List<string> chain, PipelineEmission pipe, string sfx)
    {
        foreach (var (part, _) in pipe.Ties)
        {
            chain.Add($"if ${GateVar(MeshLatch(part))} == 0");
            chain.Add($"run = CustomShaderTie_{part}_{sfx}");
            chain.Add("endif");
        }
    }

    /// <summary>Derive one pipeline's per-copy pose route and write its shaders and piece files, or null
    /// with a build-log line when the chain must stay. One palette pass per mesh the anchor is captured from
    /// — its lod0 and each tier of its own that recovers; a tier recovering nothing gathers the lod0 packet
    /// out of the lod0 capture at its own draw and runs the lod0 palette pass on it, as its chain read that
    /// capture. Each kernel mesh ships its anchor packet (<see cref="ComputeTemplates.AnchorPacket"/>) and
    /// the gather shader that fills it. The donor's draw ranges are cut into pieces of at most
    /// <paramref name="windowVertices"/> vertices (<see cref="DonorPieces"/>), each shipping its index
    /// buffer, its local-to-donor map and its rows of the UV stream and of every variant of it.
    ///
    /// <para>A pool reaching past the replaced part takes the route where <paramref name="pooled"/> places
    /// every source mesh (see <see cref="PooledPlan"/>): each source mesh adds its ring with its gather and
    /// stamp passes, its own palette pass and, where a bone's row places it, its pick pass, and the
    /// anchor's palette passes write the tie rows (<paramref name="tiePairs"/>) under every slot a source
    /// poses, so a source with no copy found at a draw leaves those rows as the tie underlay states them. Every palette pass of such a pipeline also
    /// writes the row mask a bone-placed pick reads. Any other reach past the replaced part — a wardrobe
    /// member, or a pool <paramref name="pooled"/> refuses or does not place — keeps the chain, since a
    /// capture at another mesh's draw names whichever copy drew it last.</para>
    ///
    /// <para>A bone placing a source is checked in the shipped operator of every mesh of its part that
    /// carries it: one shipped tied to another bone would place copies by that other bone's row, so the
    /// build stops instead. The plan's placement (<see cref="PlanPalettePruning"/>) predicts what ships, so
    /// this is not expected to fire.</para></summary>
    static PoseRouteEmission? PoseRouteFor(string outDir, string sfx,
        List<(string Part, int N, int Nb, int Rows)> partMeta, int anchorIdx, List<OperatorArt> partArts,
        List<(string Name, int PartIdx, uint[] Scatter, OperatorArt Art)> tierWork,
        List<(string Part, string Name, string Suffix, string Hash, int Rows, DrawShapeSet? Shapes, TierMaterialMap? Map)> tierMeta,
        List<GroupMemberEmission> groupSections, List<(string Part, int Pairs)> ties,
        IReadOnlyList<(uint Tied, uint Source)> tiePairs,
        Dictionary<string, List<(uint Tied, uint Source)>> tierOrphans,
        PooledPlan? pooled, Func<uint, int> unionSlot, Func<string, uint, Vector3> restOrigin,
        IReadOnlyDictionary<uint, string>? bonePaths,
        int paletteSlots, int vcount, int vb1Stride, IReadOnlyList<StreamVariant> vb1Variants, string ibFmt,
        List<(int Count, int Start, int Base)> draws, int windowVertices, List<string> diagnostics)
    {
        // The conversion of a single-part pool is always the witness one (a constants conversion needs a
        // second lod0 owner, which the second reason already refused), so it is not a reason here.
        int paletteRows = 4 * paletteSlots;
        bool pool = pooled is { Refusal: null, Sources.Count: > 0 };
        string? why =
            partMeta[anchorIdx].Rows == 0 ? "The replaced part supplies no bones of its own"
            : pooled?.Refusal is { } refusal ? refusal
            : !pool && partMeta.Count(p => p.Rows > 0) > 1 ? "Some of its bones come from other parts"
            : groupSections.Count > 0 ? "Some of its bones come from a wardrobe member's own draw"
            : !pool && ties.Count > 0 ? "Some of its bones follow whether other parts are on screen"
            : !pool && tierWork.Any(t => t.PartIdx != anchorIdx) ? "Another part's lower-detail mesh supplies some of its bones"
            : vcount == 0 ? "The replacement mesh has no vertices"
            : paletteRows > DonorPieces.WindowElements ? $"The replacement uses more bones than a palette row holds ({paletteSlots} of {DonorPieces.WindowElements / 4})"
            : null;
        if (why is not null)
        {
            diagnostics.Add($"{sfx}: copies of the replaced part share one pose when they are on screen together. {why}.");
            return null;
        }
        string anchor = partMeta[anchorIdx].Part;
        if (pool)
        {
            // the plan placed exactly the meshes this pipeline recovers rows from besides the anchor's own
            var placed = pooled!.Sources.Select(s => s.Mesh).ToHashSet(StringComparer.Ordinal);
            var recovering = partMeta.Where((p, i) => i != anchorIdx && p.Rows > 0).Select(p => p.Part)
                .Concat(tierWork.Where(t => t.PartIdx != anchorIdx).Select(t => t.Name)).ToHashSet(StringComparer.Ordinal);
            if (!placed.SetEquals(recovering))
                throw new InvalidOperationException($"{sfx}: the placement plan names {string.Join(", ", placed.Order())} "
                    + $"but the pipeline recovers rows from {string.Join(", ", recovering.Order())}");
            // the placing bone as it ships, in every mesh of the placing part that carries it
            foreach (var s in pooled.Sources.Where(s => s.Kind is Placement.ByAnchorBone or Placement.BySourceBone))
            {
                int by = s.Kind == Placement.ByAnchorBone ? anchorIdx : s.Placer;
                foreach (var (mesh, art) in new[] { (partMeta[by].Part, partArts[by]) }
                             .Concat(tierWork.Where(t => t.PartIdx == by).Select(t => (t.Name, t.Art))))
                    if (Array.IndexOf(art.Hashes, s.Bone) is var row and >= 0 && art.Weak[row])
                        throw new InvalidOperationException($"{sfx}: bone {BoneName(bonePaths, s.Bone)} places '{s.Mesh}', "
                            + $"but '{mesh}' recovers that bone only as a copy of another bone's row");
            }
        }
        var anchorTiers = tierWork.Where(t => t.PartIdx == anchorIdx).ToList();
        // a row a source poses takes its tie at the anchor's passes, ahead of a tier's own orphan pairs, so a
        // tie onto a row the tier does not carry follows that tier's orphan pair too
        var anchorTies = pool ? tiePairs.OrderBy(p => p.Tied).ToList() : new List<(uint Tied, uint Source)>();
        // every kernel mesh with its operator: the lod0, then each tier of the anchor that recovers rows
        var meshes = new List<(string Mesh, OperatorArt Art, IReadOnlyList<(uint Tied, uint Source)> Pairs)>
            { (anchor, partArts[anchorIdx], anchorTies) };
        var tierKernel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tierMeta.Where(t => t.Part == anchor))
        {
            var work = anchorTiers.FirstOrDefault(w => w.Name == t.Name);
            if (t.Rows == 0 || work.Art is null)
            {
                // this level's draws gather the lod0 packet out of the lod0 capture reference and run the
                // lod0 palette pass on it: the reference names whichever copy drew at full detail last,
                // not the copy drawing now
                diagnostics.Add($"{t.Name}: this lower-detail mesh supplies no bones of its own, so copies of "
                    + "the replaced part share one pose at its draws");
                tierKernel[t.Name] = anchor;
                continue;
            }
            tierKernel[t.Name] = t.Name;
            meshes.Add((t.Name, work.Art, anchorTies.Concat(tierOrphans.TryGetValue(t.Name, out var list)
                ? list.OrderBy(p => p.Tied) : Enumerable.Empty<(uint Tied, uint Source)>()).ToList()));
        }
        // per local bone, whether its row is its own recovery: the row mask's answer on the pooled route
        static bool[] Sound(OperatorArt art) => art.Weak.Select(weak => !weak).ToArray();
        // each mesh's packet: the gather's index and lookup, and a slim operator's vertex list remapped to
        // packet entries, which the palette pass reads in place of the operator's own
        var kernels = new List<(string Mesh, int Tier, int Packet, bool Slim)>();
        for (int tier = 0; tier < meshes.Count; tier++)
        {
            var (mesh, art, pairs) = meshes[tier];
            var (index, lookup, packetSel) = ComputeTemplates.AnchorPacket(art.Sel, art.N);
            bool slim = packetSel is not null;
            File.WriteAllBytes(Path.Combine(outDir, PacketIndexFile(mesh)), UIntBytes(index));
            File.WriteAllBytes(Path.Combine(outDir, PacketLookupFile(mesh)), UIntBytes(lookup));
            if (packetSel is not null)
                File.WriteAllBytes(Path.Combine(outDir, PacketSelFile(mesh)), UIntBytes(packetSel));
            File.WriteAllText(Path.Combine(outDir, GatherFile(mesh)), ComputeTemplates.EmitGather(index.Length));
            File.WriteAllText(Path.Combine(outDir, PosePaletteFile(mesh, sfx)),
                ComputeTemplates.EmitPosePalette(4 * art.Hashes.Length, slim, art.N, pairs, pool ? Sound(art) : null));
            kernels.Add((mesh, tier, index.Length, slim));
        }
        // the pooled route's source meshes, in the order their passes run: each one's packet layout, its ring
        // gather and stamp (run at its own draws), its own palette pass and, where a bone's row places it, its
        // pick
        List<PoseSource>? sources = null;
        if (pool)
        {
            sources = new List<PoseSource>();
            diagnostics.Add($"{sfx}: copies of the replaced part each keep their own pose when they are on screen "
                + "together, the parts it takes bones from included.");
            foreach (var s in pooled!.Sources)
            {
                var art = s.IsTier ? tierWork.First(w => w.Name == s.Mesh).Art : partArts[s.Part];
                var (index, lookup, packetSel) = ComputeTemplates.AnchorPacket(art.Sel, art.N);
                RequireRingFits(anchor, partMeta[s.Part].Part, index.Length);
                bool slim = packetSel is not null;
                File.WriteAllBytes(Path.Combine(outDir, PacketIndexFile(s.Mesh)), UIntBytes(index));
                File.WriteAllBytes(Path.Combine(outDir, PacketLookupFile(s.Mesh)), UIntBytes(lookup));
                if (packetSel is not null)
                    File.WriteAllBytes(Path.Combine(outDir, PacketSelFile(s.Mesh)), UIntBytes(packetSel));
                File.WriteAllText(Path.Combine(outDir, RingGatherFile(s.Mesh)),
                    ComputeTemplates.EmitRingGather(index.Length, PoseRingEntries));
                File.WriteAllText(Path.Combine(outDir, RingStampFile(s.Mesh)), ComputeTemplates.EmitRingStamp(index.Length));
                var pairs = s.IsTier && tierOrphans.TryGetValue(s.Mesh, out var orphans)
                    ? orphans.OrderBy(p => p.Tied).ToList() : new List<(uint Tied, uint Source)>();
                string PoolPalette(ComputeTemplates.PickRule rule, float cap) =>
                    ComputeTemplates.EmitPoolPosePalette(4 * art.Hashes.Length, slim, art.N, pairs, Sound(art), index.Length,
                        rule, PoseRingEntries, cap, PlacementAhead, SameRootTolerance, SameDrawTolerance);

                string palette;
                string how;
                switch (s.Kind)
                {
                    case Placement.SameRoot:
                        palette = PoolPalette(ComputeTemplates.PickRule.SameRoot, PlacementCap);
                        how = "by sharing the replaced part's position";
                        break;
                    case Placement.ByAnchorBone:
                    case Placement.BySourceBone:
                    {
                        var rest = restOrigin(s.Dir, s.Root);
                        int slot = unionSlot(s.Bone);
                        palette = PoolPalette(ComputeTemplates.PickRule.ByRow, PlacementCap);
                        File.WriteAllText(Path.Combine(outDir, PosePickFile(s.Mesh, sfx)), ComputeTemplates.EmitPosePick(
                            PoseRingEntries, PlacementCap, PlacementAhead, SameRootTolerance, SameDrawTolerance, (uint)slot,
                            (rest.X, rest.Y, rest.Z), index.Length));
                        how = s.Kind == Placement.ByAnchorBone
                            ? $"by where the replaced part's bone {BoneName(bonePaths, s.Bone)} places it"
                            : $"by where bone {BoneName(bonePaths, s.Bone)} of '{partMeta[s.Placer].Part}' places it";
                        // a lower-detail mesh of the anchor that does not carry the placing bone writes no row
                        // for it at its own draws, so the row mask leaves the source absent there
                        if (s.Kind == Placement.ByAnchorBone)
                            foreach (var (mesh, kart, _) in meshes.Skip(1))
                                if (Array.IndexOf(kart.Hashes, s.Bone) < 0)
                                    diagnostics.Add($"{mesh}: this lower-detail mesh does not recover bone "
                                        + $"{BoneName(bonePaths, s.Bone)}, so at its draws '{s.Mesh}' is treated as absent: "
                                        + "each of its bones under a bone of the replaced part moves rigidly with the "
                                        + "nearest such bone, and any other keeps its bind pose");
                        break;
                    }
                    default:
                        palette = PoolPalette(ComputeTemplates.PickRule.ByPosition, PositionCap);
                        how = "by its distance from the replaced part's position";
                        break;
                }
                File.WriteAllText(Path.Combine(outDir, PosePaletteFile(s.Mesh, sfx)), palette);
                diagnostics.Add($"{sfx}: '{s.Mesh}' is matched to each copy {how}.");
                sources.Add(new PoseSource(s.Mesh, partMeta[s.Part].Part, index.Length, slim,
                    s.Kind is Placement.ByAnchorBone or Placement.BySourceBone));
            }
            diagnostics.Add($"{sfx}: a part it takes bones from that has not drawn yet in a pass, or whose copies "
                + "are too close together to tell apart, is treated as absent for that draw: each of its bones under a "
                + "bone of the replaced part moves rigidly with the nearest such bone, and any other keeps its bind pose.");
        }
        File.WriteAllText(Path.Combine(outDir, $"pose_skin_{sfx}.hlsl"), ComputeTemplates.EmitPoseSkin(vcount));

        // the pieces: each draw range cut by its triangles, every piece shipping its own index buffer,
        // local-to-donor map and rows of stream 1 and of each variant
        byte[] ib = File.ReadAllBytes(Path.Combine(outDir, $"combined_ib_{sfx}.buf"));
        int bpi = ibFmt.Contains("R16", StringComparison.Ordinal) ? 2 : 4;
        byte[] vb1 = File.ReadAllBytes(Path.Combine(outDir, $"combined_vb1_{sfx}.buf"));
        var variants = vb1Variants.Select(v => (v.Stride, Bytes: File.ReadAllBytes(Path.Combine(outDir, v.File)))).ToList();
        var pieces = new List<PosePiece>();
        for (int range = 0; range < draws.Count; range++)
        {
            var (count, start, baseVertex) = draws[range];
            var indices = new uint[count];
            for (int i = 0; i < count; i++)
                indices[i] = bpi == 2 ? BitConverter.ToUInt16(ib, (start + i) * 2) : BitConverter.ToUInt32(ib, (start + i) * 4);
            foreach (var piece in DonorPieces.Cut(indices, baseVertex, windowVertices))
            {
                int k = pieces.Count;
                if (piece.LocalToDonor.Any(v => v >= (uint)vcount))
                    throw new InvalidOperationException($"{sfx}: draw range {range} names a vertex past the replacement's {vcount}");
                var ibBytes = new byte[(piece.Indices * 2 + 3) / 4 * 4];
                Buffer.BlockCopy(piece.LocalIndices, 0, ibBytes, 0, piece.Indices * 2);
                File.WriteAllBytes(Path.Combine(outDir, PieceFile(sfx, k, "ib")), ibBytes);
                File.WriteAllBytes(Path.Combine(outDir, PieceFile(sfx, k, "map")), UIntBytes(piece.LocalToDonor));
                File.WriteAllBytes(Path.Combine(outDir, PieceFile(sfx, k, "vb1")), Rows(vb1, vb1Stride, piece.LocalToDonor));
                for (int j = 0; j < variants.Count; j++)
                    File.WriteAllBytes(Path.Combine(outDir, PieceFile(sfx, k, $"vb1_v{j + 1}")),
                        Rows(variants[j].Bytes, variants[j].Stride, piece.LocalToDonor));
                pieces.Add(new PosePiece(k, range, piece.Vertices, piece.Indices, piece.StreamElements));
            }
        }
        if (pieces.Count > draws.Count)
            diagnostics.Add($"{sfx}: the replacement is drawn in {pieces.Count} pieces, as its {vcount} vertices exceed the {windowVertices} one stream holds");
        return new PoseRouteEmission(kernels, tierKernel, paletteRows, pieces, pieces.Max(p => p.Elements), sources);
    }

    /// <summary>The rows of a vertex stream, <paramref name="stride"/> bytes each, for the vertices named,
    /// in that order.</summary>
    static byte[] Rows(byte[] stream, int stride, uint[] vertices)
    {
        var rows = new byte[vertices.Length * stride];
        for (int i = 0; i < vertices.Length; i++)
            Buffer.BlockCopy(stream, (int)vertices[i] * stride, rows, i * stride, stride);
        return rows;
    }

    static string PosePaletteFile(string mesh, string sfx) => $"pose_palette_{mesh}_{sfx}.hlsl";
    static string PieceFile(string sfx, int piece, string what) => $"piece{piece}_{what}_{sfx}.buf";

    static string PacketIndexFile(string mesh) => $"packet_index_{mesh}.buf";
    static string PacketLookupFile(string mesh) => $"packet_lookup_{mesh}.buf";
    static string PacketSelFile(string mesh) => $"packet_sel_{mesh}.buf";
    static string GatherFile(string mesh) => $"gather_{mesh}.hlsl";

    /// <summary>The gather of one kernel mesh's anchor packet, the first pass of the block at the mesh's own
    /// draw: one point per packet entry, drawn through the index buffer so the input assembler reads each
    /// entry's vertex out of the draw's bound vertex buffer, into the packet texture. It runs inside a pose
    /// or ring block (<see cref="BlockSection"/>), which has set the shared state, saved the draw's index
    /// buffer and lookup slot and cleared the other targets, so the gather names only its own vertex shader
    /// and topology; the loader puts the block's back after it. The texture states its single slice, mip
    /// and sample, which the loader gives a texture no default for.</summary>
    static string GatherSection(string mesh, int packet, bool withLayout) =>
        PacketTexture($"Resource_Packet_{mesh}", packet) + (withLayout ? PacketLayout(mesh) : "") + "\n"
        + GatherShader($"CustomShaderGather_{mesh}", mesh, packet, source: null);

    /// <summary>The index and lookup buffers a mesh's gathers draw through: its plain gather into its packet
    /// texture where it is a replaced part, its ring gather where it is a source.</summary>
    static string PacketLayout(string mesh) =>
        $"[Resource_PacketIndex_{mesh}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {PacketIndexFile(mesh)}\n"
        + $"[Resource_PacketLookup_{mesh}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {PacketLookupFile(mesh)}\n";

    /// <summary>A texture holding a packet of <paramref name="packet"/> entries, read and rendered to.</summary>
    static string PacketTexture(string name, int packet) =>
        $"[{name}]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\n"
        + $"width = {ComputeTemplates.PacketWidth}\nheight = {ComputeTemplates.PacketHeight(packet)}\n"
        + "array = 1\nmips = 1\nmsaa = 1\n"
        + "bind_flags = shader_resource render_target\n";

    /// <summary>The anchor's lod0 gather as run at the draw of a lower-detail mesh that recovers nothing
    /// itself: the draw of <see cref="GatherSection"/>, fed from the lod0 capture reference instead of the
    /// tier's own vertex buffer, so the lod0 kernel that runs next reads this frame's lod0 pose wherever
    /// the tier's draw falls in the frame. The reference keeps the captured buffer's stride.</summary>
    static string GatherRefSection(string anchor, int packet) =>
        GatherShader($"CustomShaderGatherRef_{anchor}", anchor, packet, source: $"Resource_{anchor}_Posed");

    static string GatherShader(string section, string mesh, int packet, string? source) =>
        $"[{section}]\nvs = {GatherFile(mesh)}\nps = {GatherPixelFile}\ntopology = point_list\n"
        + $"o0 = set_viewport Resource_Packet_{mesh}\n"
        + $"ib = Resource_PacketIndex_{mesh}\nvs-t1 = Resource_PacketLookup_{mesh}\n"
        + (source is null ? "" : $"vb0 = {source}\n")
        + $"drawindexed = {packet}, 0, 0\n\n";

    /// <summary>The pixel-shader slots the pose passes bind at: packet or palette at 0, the operator and
    /// the map at 1 to 4, the replacement's bind geometry and weights at 5 and 6 (on the pooled route a
    /// source's pick and the replaced part's rows). A pose block saves the draw's own binds at the slots its
    /// passes bind when it starts and puts them back when it ends.</summary>
    static readonly int[] PosePassSlots = { 0, 1, 2, 3, 4, 5, 6 };

    /// <summary>The pose passes of one draw of the replacement: the passes that build the draw's palette
    /// (<see cref="Head"/>), then one skin pass per piece with the donor range the piece belongs to. A
    /// section runs them as one pose block (<see cref="Block"/>): in the routed draw sections where the draw
    /// routes, else in the capture chain ahead of the draw. <paramref name="Slots"/> are the pixel-shader
    /// slots the passes bind between them; <paramref name="GatherRef"/> says the block's gather rebinds the
    /// draw's first vertex buffer, which the block then saves too; every block declared goes to
    /// <paramref name="Blocks"/>.</summary>
    sealed record PosePasses(string Sfx, IReadOnlyList<string> Head, IReadOnlyList<(int Range, string Skin)> Skins,
        IReadOnlyList<int> Slots, bool GatherRef, ICollection<string> Blocks)
    {
        /// <summary>The run lines for a section that draws the ranges <paramref name="drawsRange"/> admits
        /// (every range when null): a piece of a range the section does not draw is not skinned there, since
        /// nothing in that section reads its stream and the section that draws it skins it again.</summary>
        IEnumerable<string> Lines(Func<int, bool>? drawsRange)
        {
            foreach (string line in Head) yield return line;
            foreach (var (range, skin) in Skins)
                if (drawsRange is null || drawsRange(range)) yield return skin;
        }

        /// <summary>The one run line a section draws its ranges with: the pose block holding the passes the
        /// section runs (<see cref="Lines"/>), declared once under the section's name (its
        /// <c>TextureOverride_Cap_</c> part dropped) and this pipeline's suffix.</summary>
        public string Block(string section, Func<int, bool>? drawsRange = null)
        {
            string stem = section.StartsWith("Cap_", StringComparison.Ordinal) ? section[4..] : section;
            string name = $"CustomShaderPoseBlock_{stem}_{Sfx}";
            var saves = Slots.Select(k => $"ps-t{k}").Append("ib").Append("vs-t1");
            if (GatherRef) saves = saves.Append("vb0");
            Blocks.Add(BlockSection(name, PoseFullscreenFile, saves, Lines(drawsRange)));
            return $"run = {name}";
        }
    }

    /// <summary>The pose passes at a draw whose gather is <paramref name="gather"/> (the mesh's own, or the
    /// lod0-reference gather of a tier that supplies no bones) and whose palette pass is
    /// <paramref name="kernelMesh"/>'s. On the pooled route the anchor's object-to-world rows are captured
    /// after the gather, and after the anchor's palette pass each source mesh, in order, runs its pick where
    /// a bone's row places it and its own palette pass, only in a frame the source mesh has drawn in so far:
    /// one that has not leaves the rows and the mask the anchor's palette pass wrote under its slots.</summary>
    static PosePasses PosePassesFor(PoseRouteEmission route, string gather, string kernelMesh, string sfx,
        ICollection<string> blocks)
    {
        var head = new List<string> { $"run = {gather}" };
        if (route.Sources is not null) head.Add($"run = CustomShaderPoseAnchorMat_{sfx}");
        head.Add($"run = CustomShaderPosePalette_{kernelMesh}_{sfx}");
        foreach (var source in route.Sources ?? Array.Empty<PoseSource>())
        {
            head.Add($"if ${DrewVar(source.Mesh)} == 1");
            head.AddRange(SourceRuns(source, sfx));
            head.Add("endif");
        }
        // the slots the block's passes bind between them (see PosePassSlots): the kernel's palette pass reads
        // slots 0 to 2, 3 and 4 as well where its operator is slim; a source's reads the same and 6, 5 too
        // where its pick places it, and its pick pass 0 to 3; every skin pass reads 0, 1, 5 and 6
        bool kernelSlim = route.Kernels.First(k => k.Mesh == kernelMesh).Slim;
        var sources = route.Sources ?? Array.Empty<PoseSource>();
        var slots = new SortedSet<int> { 0, 1, 2, 5, 6 };
        if (kernelSlim || sources.Any(s => s.Slim)) { slots.Add(3); slots.Add(4); }
        if (sources.Any(s => s.ByBone)) slots.Add(3);
        return new PosePasses(sfx, head, route.Pieces.Select(p => (p.Range, $"run = CustomShaderPoseSkin_{sfx}_p{p.Number}")).ToList(),
            slots.ToList(), gather.StartsWith("CustomShaderGatherRef_", StringComparison.Ordinal), blocks);
    }

    /// <summary>One source mesh's passes at a draw of the replacement, reading the mesh's ring: its pick where
    /// a bone's row places it, then its palette pass.</summary>
    static IEnumerable<string> SourceRuns(PoseSource source, string sfx)
    {
        if (source.ByBone) yield return $"run = CustomShaderPosePick_{source.Mesh}_{sfx}";
        yield return $"run = CustomShaderPosePalette_{source.Mesh}_{sfx}";
    }

    /// <summary>One block of passes at a draw: an outer custom shader that sets the state every pass in it
    /// shares (<paramref name="vs"/>, no hull, domain or geometry shader, a triangle list, no culling, no
    /// depth, no blending), saves the draw's binds at <paramref name="saves"/>, clears the depth target and
    /// every colour target but the first (a target set whose members differ in size is dropped, and the
    /// game's own targets are still bound), runs <paramref name="lines"/>, and puts the binds back. The
    /// loader itself restores the shaders, states, viewports and targets a custom shader run sets when the
    /// run ends, the block's and each pass's alike, so a pass inside the block (<see cref="InnerPass"/>)
    /// names only what differs from the block. A state group given any key is rebuilt from the D3D11
    /// defaults plus the keys given, not merged over the game's: <c>cull = none</c> alone already turns the
    /// scissor off, and <c>blend = disable</c> replaces the game's blend with an opaque write of every
    /// channel.</summary>
    static string BlockSection(string name, string vs, IEnumerable<string> saves, IEnumerable<string> lines)
    {
        var slots = saves.ToList();
        var b = new StringBuilder($"[{name}]\nvs = {vs}\nhs = null\nds = null\ngs = null\n"
            + "topology = triangle_list\ncull = none\ndepth_enable = false\nblend = disable\n");
        foreach (string slot in slots) b.Append($"{SaveResource(slot)} = ref {slot}\n");
        b.Append("od = null\n").Append(string.Concat(Enumerable.Range(1, 7).Select(k => $"o{k} = null\n")));
        foreach (string line in lines) b.Append(line).Append('\n');
        foreach (string slot in slots) b.Append($"{slot} = {SaveResource(slot)}\n");
        return b.Append('\n').ToString();
    }

    /// <summary>The resource a block saves the draw's bind at <paramref name="slot"/> in.</summary>
    static string SaveResource(string slot) => slot switch
    {
        "ib" => "Resource_SaveIB",
        "vs-t1" => "Resource_SaveVST1",
        "vs-t2" => "Resource_SaveVST2",
        "vb0" => "Resource_SaveVB0",
        _ when slot.StartsWith("ps-t", StringComparison.Ordinal) => $"Resource_SavePST{slot[4..]}",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "no save resource for this slot"),
    };

    /// <summary>One pass inside a block: the fullscreen triangle with <paramref name="ps"/>, its inputs
    /// bound, its colour target (and the viewport, from a texture) set, and the draw. The state, the slot
    /// saves and the target clears are the block's. A pass that reads the draw's own object-to-world rows
    /// names the capture vertex shader (<paramref name="vs"/>), which leaves the draw's vertex-shader
    /// constants bound; the loader puts the block's back after it.</summary>
    static string InnerPass(string section, string ps, IReadOnlyList<(int Slot, string Resource)> inputs, IEnumerable<string> targets,
        string? vs = null)
    {
        var b = new StringBuilder($"[{section}]\n");
        if (vs is not null) b.Append($"vs = {vs}\n");
        b.Append($"ps = {ps}\n");
        foreach (var (slot, resource) in inputs) b.Append($"ps-t{slot} = {resource}\n");
        foreach (string t in targets) b.Append(t).Append('\n');
        return b.Append("draw = 3, 0\n\n").ToString();
    }

    /// <summary>A pass run on its own, outside any block (the frame pass in <c>[Present]</c>): a block of
    /// one pass, with <paramref name="ps"/> and its inputs' slots saved.</summary>
    static string StandalonePass(string section, string ps, IReadOnlyList<(int Slot, string Resource)> inputs, IEnumerable<string> targets) =>
        BlockSection(section, PoseFullscreenFile, inputs.Select(i => $"ps-t{i.Slot}"),
            new[] { $"ps = {ps}" }.Concat(inputs.Select(i => $"ps-t{i.Slot} = {i.Resource}")).Concat(targets).Append("draw = 3, 0"));

    /// <summary>The palette pass of one kernel mesh: the packet its gather filled, its operator and map (and
    /// a slim operator's remapped vertex list and widths), into the palette texture. On the pooled route
    /// (<paramref name="masked"/>) the row mask is the pass's second target. A source mesh's pass
    /// (<paramref name="source"/>) reads its mesh's ring in place of the packet (the slots' packets and rows
    /// both), its pick where a bone's row places it, and the replaced part's rows.</summary>
    static string PosePaletteSection(PipelineEmission pipe, string mesh, bool slim, bool masked, PoseSource? source = null)
    {
        string sfx = pipe.Sfx;
        var inputs = new List<(int, string)>
        {
            (0, source is null ? $"Resource_Packet_{mesh}" : RingTexture(mesh)), (1, pipe.CpinvResource(mesh)),
            (2, $"Resource_{mesh}_Map_{sfx}"),
        };
        if (slim) inputs.AddRange(new[] { (3, $"Resource_{mesh}_PacketSel"), (4, $"Resource_{mesh}_Off") });
        // a source mesh's pass also reads what finds this draw's copy and the anchor's rows, which its rebase
        // goes between
        if (source is not null)
        {
            if (source.ByBone) inputs.Add((5, PickResource(mesh, sfx)));
            inputs.Add((6, AnchorMatResource(sfx)));
        }
        var targets = new List<string> { $"o0 = set_viewport Resource_PoseTex_{sfx}" };
        if (masked) targets.Add($"o1 = {MaskResource(sfx)}");
        return InnerPass($"CustomShaderPosePalette_{mesh}_{sfx}", PosePaletteFile(mesh, sfx), inputs, targets);
    }

    // ---- the pooled route's sections ------------------------------------------------------------------

    static string AnchorMatResource(string sfx) => $"Resource_AnchorMat_{sfx}";
    static string PickResource(string mesh, string sfx) => $"Resource_Pick_{mesh}_{sfx}";
    static string MaskResource(string sfx) => $"Resource_PoseMask_{sfx}";
    /// <summary>A source mesh's ring texture: <see cref="PoseRingEntries"/> slots, slot k's packet at rows
    /// k·S to k·S+H−1 and its object-to-world rows and frame number in row k·S+H.</summary>
    static string RingTexture(string mesh) => $"Resource_Ring_{mesh}";
    /// <summary>The ring slot a source mesh's next draw fills: advanced by every write and wrapped at
    /// <see cref="PoseRingEntries"/>, never reset (a reload clears the ring, so any slot reads as empty).</summary>
    static string RingSlotVar(string mesh) => $"zz_rslot_{mesh}";
    /// <summary>The per-frame flag a source mesh's ring block sets at its draw: 1 once the mesh has drawn in
    /// the frame while a pipeline reading it is on.</summary>
    static string DrewVar(string mesh) => $"zz_drew_{mesh}";

    /// <summary>A one-row texture of <paramref name="width"/> float4 pixels, read and rendered to: a
    /// capture's target, a pick's answer, a ring slot, the frame number, a ring's rows.</summary>
    static string RowsTexture(string name, int width = 5) =>
        $"[{name}]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = {width}\nheight = 1\n"
        + "array = 1\nmips = 1\nmsaa = 1\nbind_flags = shader_resource render_target\n";

    /// <summary>The replaced part's capture at each of its draws: its four object-to-world rows and this
    /// frame's number, into a 5x1 target, drawn with the draw's own vertex-shader constants left bound.</summary>
    static string AnchorMatSection(string sfx) =>
        InnerPass($"CustomShaderPoseAnchorMat_{sfx}", PoseCapturePsFile, new[] { (0, FrameResource) },
            new[] { $"o0 = set_viewport {AnchorMatResource(sfx)}" }, PoseCaptureVsFile);

    /// <summary>The lines one draw of a source mesh writes its ring with: the mesh's ring block
    /// (<see cref="RingSections"/>), then the flag saying the mesh has drawn this frame.</summary>
    static string[] RingRuns(string mesh) =>
        new[] { $"run = CustomShaderRingBlock_{mesh}", $"${DrewVar(mesh)} = 1" };

    /// <summary>One source mesh's ring, shared by every pipeline that takes rows from it: its texture
    /// (<see cref="RingTexture"/>), the gather that writes this draw's packet into a slot and the stamp that
    /// writes the draw's object-to-world rows and frame number beside it, and the ring block run at the
    /// mesh's draw: the number of the slot the mesh's next draw fills bound for both, the gather, the stamp,
    /// then the slot advanced. Neither pass reads anything rendered this frame (the draw's own vertex buffer
    /// and constants, the mod's lookup and slot files, the frame number from the last <c>[Present]</c>), so a
    /// ring write waits on no pass. The mesh's packet layout is declared beside it
    /// (<see cref="PacketLayout"/>), once whichever route needs it.</summary>
    static string RingSections(string mesh, int packet)
    {
        var b = new StringBuilder();
        b.Append($"[{RingTexture(mesh)}]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\n"
            + $"width = {ComputeTemplates.PacketWidth}\nheight = {ComputeTemplates.RingSlotRows(packet) * PoseRingEntries}\n"
            + "array = 1\nmips = 1\nmsaa = 1\nbind_flags = shader_resource render_target\n\n");
        b.Append($"[CustomShaderRingGather_{mesh}]\nvs = {RingGatherFile(mesh)}\nps = {GatherPixelFile}\ntopology = point_list\n"
            + $"o0 = set_viewport {RingTexture(mesh)}\n"
            + $"ib = Resource_PacketIndex_{mesh}\nvs-t1 = Resource_PacketLookup_{mesh}\n"
            + $"drawindexed = {packet}, 0, 0\n\n");
        b.Append(InnerPass($"CustomShaderRingStamp_{mesh}", RingStampFile(mesh), new[] { (0, FrameResource) },
            new[] { $"o0 = set_viewport {RingTexture(mesh)}" }));
        string slot = RingSlotVar(mesh);
        var lines = new List<string>();
        for (int k = 0; k < PoseRingEntries; k++)
            lines.AddRange(new[] { $"{(k == 0 ? "if" : "else if")} ${slot} == {k}",
                $"vs-t2 = {RingSlotResource(k)}", $"ps-t2 = {RingSlotResource(k)}" });
        lines.AddRange(new[]
        {
            "endif",
            $"run = CustomShaderRingGather_{mesh}",
            $"run = CustomShaderRingStamp_{mesh}",
            $"${slot} = ${slot} + 1",
            $"if ${slot} >= {PoseRingEntries}",
            $"${slot} = 0",
            "endif",
        });
        b.Append(BlockSection($"CustomShaderRingBlock_{mesh}", PoseCaptureVsFile,
            new[] { "ps-t0", "ps-t2", "ib", "vs-t1", "vs-t2" }, lines));
        return b.ToString();
    }

    /// <summary>The ring slots' numbers, one buffer per slot loaded from its file, which a ring block binds
    /// for its gather and stamp; declared once per mod.</summary>
    static string RingSlotSections() =>
        string.Concat(Enumerable.Range(0, PoseRingEntries).Select(k =>
            $"[{RingSlotResource(k)}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {RingSlotFile(k)}\n")) + "\n";

    /// <summary>The <c>[Present]</c> lines that advance the frame number: the frame pass, then the next number
    /// copied back over the frame texture (a pass never reads and writes one texture).</summary>
    static readonly string[] FramePresentLines =
        { "run = CustomShaderPoseFrame", $"{FrameResource} = copy {FrameNextResource}" };

    /// <summary>The frame number's two textures and the pass that writes the next number, which the
    /// <c>[Present]</c> command list runs and copies back (<see cref="FramePresentLines"/>).</summary>
    static string FrameSections() =>
        RowsTexture(FrameResource, 1) + RowsTexture(FrameNextResource, 1) + "\n"
        + StandalonePass("CustomShaderPoseFrame", PoseFrameFile, new[] { (0, FrameResource) },
            new[] { $"o0 = set_viewport {FrameNextResource}" });

    /// <summary>The pick pass of one source mesh a bone's row places: the palette so far, the replaced part's
    /// rows, the row mask and the mesh's ring, into the mesh's pick texture. A source placed any other way has
    /// no pick pass: its palette pass picks for itself.</summary>
    static string PosePickSection(string sfx, PoseSource source) =>
        InnerPass($"CustomShaderPosePick_{source.Mesh}_{sfx}", PosePickFile(source.Mesh, sfx),
            new[] { (0, $"Resource_PoseTex_{sfx}"), (1, AnchorMatResource(sfx)), (2, MaskResource(sfx)), (3, RingTexture(source.Mesh)) },
            new[] { $"o0 = set_viewport {PickResource(source.Mesh, sfx)}" });

    /// <summary>The skin pass of one piece: the palette, the piece's map and the replacement's bind geometry
    /// and weights, into the piece's stream buffer under the borrowed viewport.</summary>
    static string PoseSkinSection(PipelineEmission pipe, PosePiece piece)
    {
        string sfx = pipe.Sfx;
        var inputs = new List<(int, string)>
        {
            (0, $"Resource_PoseTex_{sfx}"), (1, $"Resource_PieceMap_{sfx}_p{piece.Number}"),
            (5, $"Resource_NewBind_{sfx}"), (6, $"Resource_NewSkin_{sfx}"),
        };
        return InnerPass($"CustomShaderPoseSkin_{sfx}_p{piece.Number}", $"pose_skin_{sfx}.hlsl", inputs,
            new[] { $"o0 = set_viewport Resource_PoseView_{sfx}", $"o0 = Resource_PoseRT_{sfx}_p{piece.Number}" });
    }

    /// <summary>Derive the tie underlay and write its shaders: for every donor-WEIGHTED union bone another
    /// part owns, the deepest skeleton ancestor the anchor owns — anchor-owned rows are recovered at the
    /// anchor's own draw, so the ancestor's converted row is live whenever the replacement is. A verbatim
    /// row copy is the rigid ride (rows are combined bind→posed affines; the bind-relative delta cancels).
    /// Bones with no path or no anchor-owned ancestor keep the identity seed, named in the build log —
    /// bind-pose placement, strictly tamer than the unwritten-recover garbage the gate retired. One shader
    /// per owner part, pairs in ascending tied-slot order, parts in pool order: rebuilds reproduce.</summary>
    static List<(string Part, int Pairs)> TieUnderlay(string outDir, string sfx, PoolMath.UnionResult union,
        int anchorIdx, List<string> parts, HashSet<int> donorSlots,
        IReadOnlyDictionary<uint, string>? bonePaths, List<string> diagnostics,
        List<(uint Tied, uint Source)>? pairsOut = null)
    {
        var ties = new List<(string Part, int Pairs)>();
        var anchorPaths = new List<(string Path, int Slot)>();
        if (bonePaths is not null)
            for (int u = 0; u < union.UnionHashes.Length; u++)
                if (union.Owner[u] == anchorIdx && bonePaths.TryGetValue(union.UnionHashes[u], out var ap))
                    anchorPaths.Add((ap, u));
        var pairsByOwner = new Dictionary<int, List<(uint Tied, uint Ancestor)>>();
        // Rows with no tie must still be WRITTEN while their owner is absent: the converts rewrite every
        // union row unconditionally (constants-K through an absent part's never-filled CB is zero — the
        // collapse this underlay exists to end; witness-K rides an arbitrary bone), so "keeps the seed"
        // is only true if this dispatch puts the identity back after them.
        var seedsByOwner = new Dictionary<int, List<uint>>();
        void Seed(int owner, uint slot)
        {
            if (!seedsByOwner.TryGetValue(owner, out var list)) seedsByOwner[owner] = list = new List<uint>();
            list.Add(slot);
        }
        for (int u = 0; u < union.UnionHashes.Length; u++)
        {
            if (union.Owner[u] == anchorIdx || !donorSlots.Contains(u)) continue;
            uint hash = union.UnionHashes[u];
            string owner = parts[union.Owner[u]];
            if (bonePaths is null || !bonePaths.TryGetValue(hash, out var path))
            {
                Seed(union.Owner[u], (uint)u);
                diagnostics.Add($"{sfx}: bone {BoneName(bonePaths, hash)} has no skeleton path — donor weight on it keeps "
                    + $"the bind-pose seed while '{owner}' is absent");
                continue;
            }
            int best = -1, bestLen = -1;
            foreach (var (ap, slot) in anchorPaths)
                if (ap.Length > bestLen && path.Length > ap.Length + 1 && path[ap.Length] == '/'
                    && path.StartsWith(ap, StringComparison.Ordinal))
                { best = slot; bestLen = ap.Length; }
            if (best < 0)
            {
                Seed(union.Owner[u], (uint)u);
                diagnostics.Add($"{sfx}: bone {BoneName(bonePaths, hash)} has no anchor-owned skeleton ancestor — donor "
                    + $"weight on it keeps the bind-pose seed while '{owner}' is absent");
                continue;
            }
            if (!pairsByOwner.TryGetValue(union.Owner[u], out var list))
                pairsByOwner[union.Owner[u]] = list = new List<(uint, uint)>();
            list.Add(((uint)u, (uint)best));
            diagnostics.Add($"{sfx}: bone {BoneName(bonePaths, hash)} rides its ancestor {BoneName(bonePaths, union.UnionHashes[best])} "
                + $"rigidly while '{owner}' is absent");
        }
        foreach (int owner in pairsByOwner.Keys.Concat(seedsByOwner.Keys).Distinct().OrderBy(k => k))
        {
            var pairs = pairsByOwner.GetValueOrDefault(owner) ?? new List<(uint, uint)>();
            var seeds = seedsByOwner.GetValueOrDefault(owner) ?? new List<uint>();
            pairsOut?.AddRange(pairs);
            File.WriteAllText(Path.Combine(outDir, $"tiefill_{parts[owner]}_{sfx}.hlsl"),
                ComputeTemplates.EmitTieFill(pairs, seeds));
            ties.Add((parts[owner], pairs.Count + seeds.Count));
        }
        return ties;
    }

    /// <summary>One tier's orphan rows and their copy sources. An orphan is a donor-WEIGHTED union row the
    /// tier's part owns that the tier writes no row for: its scatter map names no bone for it, because the
    /// tier's rig lacks the bone or the bone's recovery there fell back to the sentinel. The source is a
    /// row the same tier DOES write into the union, chosen on the part's lod0 skin — the co-riding bone
    /// with the most shared weight, else the nearest by weighted support centroid, else the tier's first
    /// written row — so the fill is the rigid ride the game's own lower-detail mesh approximates. Rows
    /// redirected to a reserved witness slot are not sources: the chain writes the slot, not the row.
    /// Pairs are (tied, source) compact union rows in ascending tied order.</summary>
    static List<(uint Tied, uint Source)> TierOrphanTies(string tierName, uint[] scatter,
        PoolMath.UnionResult union, int owner, HashSet<int> donorRows, StreamsLoad lod0, uint[] lod0Hashes,
        IReadOnlyDictionary<uint, string>? bonePaths, List<string> diagnostics)
    {
        int ub = union.UnionHashes.Length;
        var written = new SortedSet<int>();
        foreach (uint s in scatter)
            if (s != PoolMath.Sentinel && s < (uint)ub) written.Add((int)s);
        var pairs = new List<(uint, uint)>();
        if (written.Count == 0) return pairs;

        int n = lod0.P.GetLength(0);
        // lod0-local bone of each written row (a row the lod0 rig lacks has no co-weight and no support)
        var localOf = new Dictionary<int, int>();
        foreach (int s in written)
        {
            int ls = Array.IndexOf(lod0Hashes, union.UnionHashes[s]);
            if (ls >= 0) localOf[s] = ls;
        }
        double[,]? centroid = null;
        double[]? wsum = null;
        void Support()
        {
            if (centroid is not null) return;
            centroid = new double[lod0.Nb, 3];
            wsum = new double[lod0.Nb];
            for (int v = 0; v < n; v++)
                for (int k = 0; k < 4; k++)
                {
                    double w = lod0.W[v, k];
                    if (w <= 0) continue;
                    int b = lod0.BI[v, k];
                    wsum[b] += w;
                    for (int j = 0; j < 3; j++) centroid[b, j] += w * lod0.P[v, j];
                }
            for (int b = 0; b < lod0.Nb; b++)
                if (wsum[b] > 0)
                    for (int j = 0; j < 3; j++) centroid[b, j] /= wsum[b];
        }

        for (int u = 0; u < ub; u++)
        {
            if (union.Owner[u] != owner || !donorRows.Contains(u) || written.Contains(u)) continue;
            uint hash = union.UnionHashes[u];
            int local = Array.IndexOf(lod0Hashes, hash);
            var co = new Dictionary<int, double>();
            if (local >= 0)
                for (int v = 0; v < n; v++)
                {
                    double wb = 0;
                    for (int k = 0; k < 4; k++)
                        if (lod0.BI[v, k] == local && lod0.W[v, k] > wb) wb = lod0.W[v, k];
                    if (wb <= 0) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        if (lod0.W[v, k] <= 0 || lod0.BI[v, k] == local) continue;
                        foreach (var (s, ls) in localOf)
                            if (ls == lod0.BI[v, k]) co[s] = co.GetValueOrDefault(s) + wb * lod0.W[v, k];
                    }
                }
            int best = -1;
            double bestScore = 0;
            foreach (int s in written)
                if (co.GetValueOrDefault(s) > bestScore) { bestScore = co[s]; best = s; }
            string how;
            if (best >= 0 && bestScore >= TieCoWeightFloor) how = "co-riding";
            else
            {
                best = -1;
                Support();
                if (local >= 0 && wsum![local] > 0)
                {
                    double bestD = double.MaxValue;
                    foreach (var (s, ls) in localOf)
                    {
                        if (wsum[ls] <= 0) continue;
                        double d = 0;
                        for (int j = 0; j < 3; j++)
                        {
                            double dd = centroid![local, j] - centroid[ls, j];
                            d += dd * dd;
                        }
                        if (d < bestD) { bestD = d; best = s; }
                    }
                }
                if (best >= 0) how = "nearest";
                else { best = written.Min; how = "first written"; }
            }
            pairs.Add(((uint)u, (uint)best));
            diagnostics.Add($"{tierName}: bone {BoneName(bonePaths, hash)} has no row at this tier — its converted row copies "
                + $"{how} bone {BoneName(bonePaths, union.UnionHashes[best])} at this tier's draws");
        }
        return pairs;
    }

    /// <summary>Move a compiled donor's group-bone indices off the dense continuation of the union and onto
    /// the palette slots the emission reserved for them: <c>unionBones + k</c> becomes
    /// <c>groupBase + k</c>, in place. An index past the continuation is left alone — the range warning
    /// above is what names it.</summary>
    static void RemapSkinIndices(byte[] skin, int compiledUnionBones, int[] oldToCompact,
        uint groupBase, int[] oldGroupToCompact, string suffix)
    {
        for (int o = 16; o + 16 <= skin.Length; o += 32)
            for (int k = 0; k < 4; k++)
            {
                float weight = BitConverter.ToSingle(skin, o - 16 + k * 4);
                uint bi = BitConverter.ToUInt32(skin, o + k * 4);
                int mapped = -1;
                if (bi < (uint)compiledUnionBones)
                    mapped = oldToCompact[bi];
                else if (bi >= (uint)compiledUnionBones
                    && bi - (uint)compiledUnionBones < (uint)oldGroupToCompact.Length)
                {
                    int group = (int)(bi - (uint)compiledUnionBones);
                    if (oldGroupToCompact[group] >= 0)
                        mapped = checked((int)groupBase + oldGroupToCompact[group]);
                }
                if (mapped < 0)
                {
                    if (weight > 0)
                        throw new InvalidDataException($"{suffix}: positive donor weight references pruned or "
                            + $"out-of-range palette row {bi}");
                    mapped = 0;
                }
                BitConverter.GetBytes((uint)mapped).CopyTo(skin, o + k * 4);
        }
    }

    static List<PalettePrunePlan> PlanPalettePruning(PoolBuildRequest req, int[] anchorOf,
        Func<string, StreamsLoad> load, Func<string, PoolMath.UnionInput> unionInput,
        IReadOnlyDictionary<(string Name, string Dir), OperatorSolve> analysis,
        List<string> diagnostics,
        out Dictionary<(string Name, string Dir), HashSet<int>> globalRows)
    {
        var allPaths = BonePathsOf(req);
        var allRows = new Dictionary<(string Name, string Dir), HashSet<int>>();
        var plans = new List<PalettePrunePlan>(req.Pipelines.Count);

        OperatorArt Art(string name, string dir)
        {
            var solve = analysis[(name, dir)];
            solve.Error?.Throw();
            return solve.Art!;
        }
        static bool Sound(OperatorArt art, uint hash)
        {
            int row = Array.IndexOf(art.Hashes, hash);
            return row >= 0 && !art.Weak[row];
        }
        void Demand(PalettePrunePlan plan, string name, string dir, int row)
        {
            var key = (name, dir);
            if (!plan.SourceRows.TryGetValue(key, out var local))
                plan.SourceRows[key] = local = new HashSet<int>();
            local.Add(row);
            if (!allRows.TryGetValue(key, out var all))
                allRows[key] = all = new HashSet<int>();
            all.Add(row);
        }
        void DemandHash(PalettePrunePlan plan, string name, string dir, uint hash)
        {
            int row = Array.IndexOf(unionInput(dir).Hashes, hash);
            if (row < 0)
                throw new InvalidOperationException($"{name}: selected recovery bone {BoneName(allPaths, hash)} is absent "
                    + "from its source bone table");
            Demand(plan, name, dir, row);
        }
        // Whether a pool part's or tier's operator will ship the bone with a slim selection of its own: the
        // bone is carried, sound, and held by the slim search (HoldsSlim, which the full solve cannot do
        // worse than). A bone it would otherwise ship at every vertex is tied there, so it is no witness and
        // no owner's first choice.
        var slimVerdicts = new Dictionary<(string Dir, uint Hash), bool>();
        bool Holds(string name, string dir, uint hash)
        {
            if (slimVerdicts.TryGetValue((dir, hash), out bool held)) return held;
            var art = Art(name, dir);
            int row = Array.IndexOf(art.Hashes, hash);   // a classification art keeps every local row, in order
            held = row >= 0 && HoldsSlim(load(dir), art.Weak, row);
            return slimVerdicts[(dir, hash)] = held;
        }
        var weightOn = new Dictionary<string, double[]>(StringComparer.Ordinal);
        double WeightOn(string dir, uint hash)
        {
            if (!weightOn.TryGetValue(dir, out var summed)) weightOn[dir] = summed = SummedWeights(load(dir));
            int row = Array.IndexOf(unionInput(dir).Hashes, hash);
            return row >= 0 && row < summed.Length ? summed[row] : 0;
        }
        // A used bone its owner could ship only at every vertex of its mesh moves to the pool part that holds
        // it with a slim selection, the one carrying the most weight on it (the first on ties, as the weight
        // argmax). When no part holds it, the owner keeps it and its operator ties it to a co-riding bone.
        PoolMath.UnionResult OwnBySlimHold(ReplacePipeline pipe, PoolMath.UnionResult union, IEnumerable<int> used)
        {
            var owner = (int[])union.Owner.Clone();
            bool moved = false;
            foreach (int u in used.OrderBy(u => u))
            {
                int from = owner[u];
                uint h = union.UnionHashes[u];
                if (Holds(pipe.Parts[from].Name, pipe.Parts[from].DumpDir, h)) continue;
                int to = -1;
                double toWeight = 0;
                for (int pi = 0; pi < pipe.Parts.Count; pi++)
                {
                    var p = pipe.Parts[pi];
                    if (pi == from || !Holds(p.Name, p.DumpDir, h)) continue;
                    double w = WeightOn(p.DumpDir, h);
                    if (to < 0 || w > toWeight) { to = pi; toWeight = w; }
                }
                if (to < 0) continue;
                owner[u] = to;
                moved = true;
                diagnostics.Add($"{pipe.Suffix}: bone {BoneName(pipe.BonePaths, h)} is recovered from '{pipe.Parts[to].Name}' instead of "
                    + $"'{pipe.Parts[from].Name}', which needs every one of its vertices to recover it");
            }
            return moved ? PoolMath.WithOwner(union, owner) : union;
        }

        for (int pipeIdx = 0; pipeIdx < req.Pipelines.Count; pipeIdx++)
        {
            var pipe = req.Pipelines[pipeIdx];
            var inputs = pipe.Parts.Select(p => unionInput(p.DumpDir)).ToList();
            var union = PoolMath.BuildUnion(inputs);
            union = PoolMath.PreferAnchorOwnership(union, anchorOf[pipeIdx],
                Art(pipe.Parts[anchorOf[pipeIdx]].Name, pipe.Parts[anchorOf[pipeIdx]].DumpDir).Weak);
            var plan = new PalettePrunePlan
            {
                FullUnion = union,
                SkinUnionRows = new HashSet<int>(),
                RetainedUnionRows = new HashSet<int>(),
                UsedGroupRows = new HashSet<int>(),
                SourceRows = new Dictionary<(string Name, string Dir), HashSet<int>>(),
                PoolSources = new HashSet<(string Name, string Dir)>(),
                TierSources = new HashSet<(string Name, string Dir)>(),
                GroupSources = new HashSet<(string Name, string Dir)>(),
            };
            plans.Add(plan);

            int compiledUnionBones = union.UnionHashes.Length;
            int compiledGroupBones = (pipe.Groups ?? Array.Empty<PoolGroup>()).Sum(g => g.GroupBones.Count);
            if (pipe.DonorDir is null)
            {
                // Test/API-only identity route: deliberately outside palette pruning. It retains the full
                // union and every recovery source exactly as the legacy emitter did.
                plan.SkinUnionRows.UnionWith(Enumerable.Range(0, compiledUnionBones));
                plan.RetainedUnionRows.UnionWith(plan.SkinUnionRows);
                plan.FullUnion = OwnBySlimHold(pipe, union, plan.SkinUnionRows);
                plan.UsedGroupRows.UnionWith(Enumerable.Range(0, compiledGroupBones));
                foreach (var p in pipe.Parts)
                {
                    plan.PoolSources.Add((p.Name, p.DumpDir));
                    for (int row = 0; row < load(p.DumpDir).Nb; row++) Demand(plan, p.Name, p.DumpDir, row);
                }
                foreach (var t in pipe.Tiers ?? Array.Empty<PoolTier>())
                {
                    plan.TierSources.Add((t.Name, t.DumpDir));
                    for (int row = 0; row < load(t.DumpDir).Nb; row++) Demand(plan, t.Name, t.DumpDir, row);
                }
                foreach (var m in GroupMeshes(pipe))
                {
                    plan.GroupSources.Add((m.Name, m.DumpDir));
                    for (int row = 0; row < load(m.DumpDir).Nb; row++) Demand(plan, m.Name, m.DumpDir, row);
                }
                continue;
            }

            var (weights, indices) = PoolMath.ParseSkin(
                File.ReadAllBytes(Path.Combine(pipe.DonorDir, "stream2.buf")));
            for (int v = 0; v < indices.GetLength(0); v++)
                for (int lane = 0; lane < 4; lane++)
                {
                    if (weights[v, lane] <= 0) continue;
                    int row = indices[v, lane];
                    if (row < 0 || row >= compiledUnionBones + compiledGroupBones)
                        throw new InvalidDataException($"{pipe.Suffix}: positive donor weight references palette "
                            + $"row {unchecked((uint)row)}, but the compiled union and coverage continuation contain only "
                            + $"{compiledUnionBones + compiledGroupBones} rows. Recompile the donor against THIS union.");
                    if (row < compiledUnionBones)
                    {
                        plan.SkinUnionRows.Add(row);
                        plan.RetainedUnionRows.Add(row);
                    }
                    else
                        plan.UsedGroupRows.Add(row - compiledUnionBones);
                }
            plan.FullUnion = union = OwnBySlimHold(pipe, union, plan.SkinUnionRows);

            var parts = pipe.Parts.Select(p => p.Name).ToList();
            foreach (int old in plan.SkinUnionRows)
            {
                int owner = union.Owner[old];
                var source = pipe.Parts[owner];
                DemandHash(plan, source.Name, source.DumpDir, union.UnionHashes[old]);
                plan.PoolSources.Add((source.Name, source.DumpDir));
            }

            // A tier supplies only the retained rows its base part owns and the tier can actually recover.
            foreach (var tier in pipe.Tiers ?? Array.Empty<PoolTier>())
            {
                int owner = parts.IndexOf(tier.Part);
                if (owner < 0) continue;
                var art = Art(tier.Name, tier.DumpDir);
                foreach (int old in plan.SkinUnionRows.Where(u => union.Owner[u] == owner))
                {
                    int row = Array.IndexOf(art.Hashes, union.UnionHashes[old]);
                    if (row >= 0 && (!art.Weak[row] || art.TieFullRows[row] >= 0))
                    {
                        Demand(plan, tier.Name, tier.DumpDir, row);
                        plan.TierSources.Add((tier.Name, tier.DumpDir));
                    }
                }
                // A shipped tier operator must keep its positively weighted rows outside the full union:
                // emission consumes their upstream tier-row verdicts and scatters them to the write-nothing
                // sentinel. Pruning one would silently discard the refusal/folded-geometry contract the
                // tier map records.
                if (plan.TierSources.Contains((tier.Name, tier.DumpDir)))
                {
                    var tierWeights = SummedWeights(load(tier.DumpDir));
                    for (int row = 0; row < art.Hashes.Length; row++)
                        if (tierWeights[row] > 0 && Array.IndexOf(union.UnionHashes, art.Hashes[row]) < 0)
                            Demand(plan, tier.Name, tier.DumpDir, row);
                }
            }

            List<(string Name, string Dir, OperatorArt Art)> ActiveOps(string part)
            {
                int pi = parts.IndexOf(part);
                var p = pipe.Parts[pi];
                var result = new List<(string, string, OperatorArt)> { (p.Name, p.DumpDir, Art(p.Name, p.DumpDir)) };
                foreach (var tier in pipe.Tiers ?? Array.Empty<PoolTier>())
                    if (string.Equals(tier.Part, part, StringComparison.Ordinal)
                        && plan.SourceRows.TryGetValue((tier.Name, tier.DumpDir), out var rows)
                        && rows.Count > 0)
                        result.Add((tier.Name, tier.DumpDir, Art(tier.Name, tier.DumpDir)));
                return result;
            }
            void RetainWitness(uint witness,
                IEnumerable<(string Name, string Dir, OperatorArt Art)> sourceOps)
            {
                int old = Array.IndexOf(union.UnionHashes, witness);
                if (old < 0)
                    throw new InvalidOperationException($"{pipe.Suffix}: witness {BoneName(pipe.BonePaths, witness)} is outside the union");
                bool added = plan.RetainedUnionRows.Add(old);
                foreach (var op in sourceOps) DemandHash(plan, op.Name, op.Dir, witness);
                foreach (var op in sourceOps)
                {
                    if (pipe.Parts.Any(p => p.Name == op.Name && p.DumpDir == op.Dir))
                        plan.PoolSources.Add((op.Name, op.Dir));
                    if ((pipe.Tiers ?? Array.Empty<PoolTier>()).Any(t => t.Name == op.Name && t.DumpDir == op.Dir))
                        plan.TierSources.Add((op.Name, op.Dir));
                }
                if (added && !plan.SkinUnionRows.Contains(old))
                {
                    plan.WitnessLines.Add($"{pipe.Suffix}: palette retains donor-unused witness row {BoneName(pipe.BonePaths, witness)} "
                        + "to preserve live recovery quality");
                    diagnostics.Add(plan.WitnessLines[^1]);
                }
            }

            var anchorOps = ActiveOps(parts[anchorOf[pipeIdx]]);
            var liveOwners = plan.SkinUnionRows.Select(u => union.Owner[u])
                .Where(pi => pi != anchorOf[pipeIdx]).Distinct().OrderBy(pi => pi).ToList();
            foreach (int owner in liveOwners)
            {
                var ownerOps = ActiveOps(parts[owner]);
                uint witness = 0;
                bool found = false;
                foreach (uint hash in Art(pipe.Parts[anchorOf[pipeIdx]].Name,
                             pipe.Parts[anchorOf[pipeIdx]].DumpDir).Hashes)
                    if (ownerOps.All(op => Holds(op.Name, op.Dir, hash))
                        && anchorOps.All(op => Holds(op.Name, op.Dir, hash)))
                    { witness = hash; found = true; break; }
                if (!found) continue;
                RetainWitness(witness, ownerOps.Concat(anchorOps));
            }

            // Coverage-group rows are independently live by the compiled continuation. Each member mesh
            // retains only group rows it soundly supplies, plus the same witness support the legacy path
            // would have selected for a surviving source.
            int groupAt = 0;
            foreach (var group in pipe.Groups ?? Array.Empty<PoolGroup>())
            {
                var liveBones = group.GroupBones.Select((hash, i) => (Hash: hash, Old: groupAt + i))
                    .Where(x => plan.UsedGroupRows.Contains(x.Old)).ToList();
                groupAt += group.GroupBones.Count;
                foreach (var member in group.Members)
                {
                    var meshes = member.Meshes ?? Array.Empty<PoolGroupMesh>();
                    var lod0 = meshes.FirstOrDefault(m => m.IsLod0);
                    if (lod0 is null) continue;

                    List<int> Supplied(PoolGroupMesh mesh)
                    {
                        var art = Art(mesh.Name, mesh.DumpDir);
                        return liveBones.Select(x => Array.IndexOf(art.Hashes, x.Hash))
                            .Where(row => row >= 0 && !art.Weak[row]).Distinct().OrderBy(row => row).ToList();
                    }

                    var lod0Rows = Supplied(lod0);
                    foreach (int row in lod0Rows) Demand(plan, lod0.Name, lod0.DumpDir, row);
                    if (lod0Rows.Count > 0) plan.GroupSources.Add((lod0.Name, lod0.DumpDir));
                    if (lod0Rows.Count == 0)
                    {
                        var art = Art(lod0.Name, lod0.DumpDir);
                        foreach (var live in liveBones)
                        {
                            int row = Array.IndexOf(art.Hashes, live.Hash);
                            diagnostics.Add(row < 0
                                ? $"{pipe.Suffix}: {lod0.Name} does not carry bone {BoneName(pipe.BonePaths, live.Hash)}, so it writes no rows for it"
                                : $"{pipe.Suffix}: {lod0.Name} recovers bone {BoneName(pipe.BonePaths, live.Hash)} ill-conditioned, so it writes no rows for it");
                        }
                    }
                    if (lod0Rows.Count > 0)
                    {
                        var lod0Art = Art(lod0.Name, lod0.DumpDir);
                        uint witness = 0;
                        bool found = false;
                        foreach (uint hash in Art(pipe.Parts[anchorOf[pipeIdx]].Name,
                                     pipe.Parts[anchorOf[pipeIdx]].DumpDir).Hashes)
                            if (Sound(lod0Art, hash) && anchorOps.All(op => Holds(op.Name, op.Dir, hash)))
                            { witness = hash; found = true; break; }
                        if (found)
                        {
                            DemandHash(plan, lod0.Name, lod0.DumpDir, witness);
                            RetainWitness(witness, anchorOps);
                        }
                    }

                    var tierRows = meshes.Where(m => !m.IsLod0)
                        .Select(m => (Mesh: m, Rows: Supplied(m), Art: Art(m.Name, m.DumpDir)))
                        .Where(x => x.Rows.Count > 0).ToList();
                    if (tierRows.Count == 0) continue;
                    uint tierWitness = 0;
                    bool tierFound = false;
                    foreach (uint hash in Art(pipe.Parts[anchorOf[pipeIdx]].Name,
                                 pipe.Parts[anchorOf[pipeIdx]].DumpDir).Hashes)
                        if (tierRows.All(x => Sound(x.Art, hash)) && anchorOps.All(op => Holds(op.Name, op.Dir, hash)))
                        { tierWitness = hash; tierFound = true; break; }
                    if (!tierFound) continue;
                    foreach (var tier in tierRows)
                    {
                        foreach (int row in tier.Rows) Demand(plan, tier.Mesh.Name, tier.Mesh.DumpDir, row);
                        DemandHash(plan, tier.Mesh.Name, tier.Mesh.DumpDir, tierWitness);
                        plan.GroupSources.Add((tier.Mesh.Name, tier.Mesh.DumpDir));
                    }
                    RetainWitness(tierWitness, anchorOps);
                }
            }

            // ---- the pooled pose route: how each copy finds its own copy of every part the replacement
            // takes rows from, decided here because the row that places a part must be recovered with the
            // rest of the palette (retained and demanded as the witness rows above are) ---------------
            if (liveOwners.Count > 0)
                plan.Pooled = PlacePooled(pipe, anchorOf[pipeIdx], union, liveOwners);

            PooledPlan PlacePooled(ReplacePipeline pp, int anchor, PoolMath.UnionResult u0, List<int> owners)
            {
                var pooled = new PooledPlan();
                if (plan.UsedGroupRows.Count > 0)
                {
                    pooled.Refusal = "Some of its bones come from a wardrobe member's own draw";
                    return pooled;
                }
                var anchorPart = pp.Parts[anchor];
                uint? anchorRoot = anchorPart.RootChain is { Count: > 0 } ac ? ac[0] : null;
                // every source mesh: each owning part's lod0, then each of its tiers that recovers rows
                var meshes = new List<(string Name, string Dir, int Part, bool Tier, IReadOnlyList<uint>? Chain)>();
                foreach (int owner in owners)
                {
                    var op = pp.Parts[owner];
                    meshes.Add((op.Name, op.DumpDir, owner, false, op.RootChain));
                    foreach (var t in pp.Tiers ?? Array.Empty<PoolTier>())
                        if (string.Equals(t.Part, op.Name, StringComparison.Ordinal)
                            && plan.TierSources.Contains((t.Name, t.DumpDir)))
                            meshes.Add((t.Name, t.DumpDir, owner, true, t.RootChain));
                }
                foreach (var m in meshes)
                {
                    if (m.Chain is not { Count: > 0 } chain)
                    {
                        pooled.Refusal = $"Which bone '{m.Name}' is attached to is not known";
                        return pooled;
                    }
                    if (Array.IndexOf(unionInput(m.Dir).Hashes, chain[0]) < 0)
                    {
                        pooled.Refusal = $"'{m.Name}' does not list the bone it is attached to";
                        return pooled;
                    }
                }
                // A part places a bone where it owns it and every one of its recoveries carrying the bone will
                // ship it as its own row: judged by the slim-hold predictor (Holds), the verdict the shipped
                // operators follow, not by the dense classification, which can call a bone sound that the
                // shipped operator ties to another.
                bool OwnsSound(int partIdx, uint hash)
                {
                    int old = Array.IndexOf(u0.UnionHashes, hash);
                    if (old < 0 || u0.Owner[old] != partIdx) return false;
                    var part = pp.Parts[partIdx];
                    return Holds(part.Name, part.DumpDir, hash)
                        && ActiveOps(part.Name).Skip(1).Where(op => Array.IndexOf(unionInput(op.Dir).Hashes, hash) >= 0)
                            .All(op => Holds(op.Name, op.Dir, hash));
                }
                var placed = new List<SourcePlacement>();
                foreach (var m in meshes)
                {
                    var chain = m.Chain!;
                    var verdict = new SourcePlacement(m.Name, m.Dir, m.Part, m.Tier, chain[0], Placement.ByAnchorPosition, 0, -1);
                    if (anchorRoot == chain[0]) verdict = verdict with { Kind = Placement.SameRoot };
                    else
                        // one walk up from the root's parent: the nearest ancestor that the anchor, or failing
                        // the anchor another part the replacement takes rows from, owns and recovers places it
                        for (int k = 1; k < chain.Count && verdict.Kind == Placement.ByAnchorPosition; k++)
                        {
                            if (OwnsSound(anchor, chain[k]))
                            {
                                verdict = verdict with { Kind = Placement.ByAnchorBone, Bone = chain[k] };
                                break;
                            }
                            int old = Array.IndexOf(u0.UnionHashes, chain[k]);
                            if (old < 0) continue;
                            int by = u0.Owner[old];
                            if (by != anchor && by != m.Part && owners.Contains(by) && OwnsSound(by, chain[k]))
                                verdict = verdict with { Kind = Placement.BySourceBone, Bone = chain[k], Placer = by };
                        }
                    placed.Add(verdict);
                }
                // a part placed by another part's row runs after it, so the row is this draw's when read;
                // chains only go up, so a cycle means two parts' tables disagree about the skeleton
                var order = new List<int>();
                var pending = new List<int>(owners);
                while (pending.Count > 0)
                {
                    int next = pending.FirstOrDefault(pi => placed.Where(v => v.Part == pi && v.Kind == Placement.BySourceBone)
                        .All(v => order.Contains(v.Placer)), -1);
                    if (next < 0)
                    {
                        pooled.Refusal = "Two of the parts it takes bones from are each attached to the other";
                        return pooled;
                    }
                    order.Add(next);
                    pending.Remove(next);
                }
                // within a part its lower-detail meshes first and its lod0 last, so where both drew this frame
                // the full-detail rows are the ones that stay
                foreach (int pi in order)
                {
                    pooled.Sources.AddRange(placed.Where(v => v.Part == pi && v.IsTier));
                    pooled.Sources.AddRange(placed.Where(v => v.Part == pi && !v.IsTier));
                }
                foreach (var v in pooled.Sources)
                {
                    if (v.Kind is not (Placement.ByAnchorBone or Placement.BySourceBone)) continue;
                    var by = pp.Parts[v.Kind == Placement.ByAnchorBone ? anchor : v.Placer];
                    int old = Array.IndexOf(u0.UnionHashes, v.Bone);
                    bool added = plan.RetainedUnionRows.Add(old);
                    // every recovery of the placing part that carries the bone recovers it, so the row is
                    // written at whichever of its meshes this draw's palette comes from
                    foreach (var op in ActiveOps(by.Name))
                        if (Array.IndexOf(unionInput(op.Dir).Hashes, v.Bone) >= 0)
                            DemandHash(plan, op.Name, op.Dir, v.Bone);
                    if (added && !plan.SkinUnionRows.Contains(old))
                    {
                        plan.PlacementLines.Add($"{pp.Suffix}: palette retains donor-unused placement row "
                            + $"{BoneName(pp.BonePaths, v.Bone)} to match '{v.Mesh}' to each copy");
                        diagnostics.Add(plan.PlacementLines[^1]);
                    }
                }
                return pooled;
            }

            if (plan.RetainedUnionRows.Count == 0 && plan.UsedGroupRows.Count == 0)
                throw new InvalidDataException($"{pipe.Suffix}: the final compiled donor skin has no positive "
                    + "palette weights; an empty palette cannot be emitted safely");
            if (plan.RetainedUnionRows.Count == 0)
                throw new InvalidDataException($"{pipe.Suffix}: the shipped conversion pipeline has an empty compact "
                    + "union, so its anchor has no retained palette row or constant-buffer resource");
        }
        globalRows = allRows;
        return plans;
    }

    public Result Build(PoolBuildRequest req)
    {
        var warnings = new List<string>();
        var diagnostics = new List<string>();
        var mergedTierWarnings = new List<MergedTierWarning>();
        var reqRigids = req.Rigids ?? Array.Empty<RigidReplace>();
        if (req.Pipelines.Count == 0 && reqRigids.Count == 0)
            throw new InvalidOperationException("pooled build with no Replace pipelines");
        // one suffix names one replacement's shipped files, whichever route it took
        var suffixes = req.Pipelines.Select(p => p.Suffix).Concat(reqRigids.Select(r => r.Suffix)).ToList();
        if (suffixes.Distinct(StringComparer.Ordinal).Count() != suffixes.Count)
            throw new InvalidOperationException("pipeline suffixes must be unique: "
                + string.Join(", ", suffixes));
        Directory.CreateDirectory(req.OutDir);

        // shared per-part artifacts — a part pooled by several pipelines is loaded, conditioned (cpinv),
        // and shader-stamped ONCE; only the scatter map is per pipeline, since it targets that pipeline's
        // union. Same for tier operators. Keyed by emission name; a name reappearing with a different
        // dump dir is a caller bug (two different meshes under one identity).
        // concurrent: the operator solve reads dumps from several threads. Bind-space conversion is a
        // property of the POOL (reference part included), not of the dump: a shared dump stays verbatim
        // on disk and every reader restates it on the way in.
        // Each pipeline's anchor as an index into its own pool parts, resolved once and threaded from here:
        // the bind-space reconciliation, the union scatter, the witness pass and the emitted chains all key
        // off it, and a second derivation is a second chance to disagree. A null anchor names the LAST part.
        // The one refusal for an anchor the pool doesn't carry, ahead of every consumer.
        var anchorOf = new int[req.Pipelines.Count];
        for (int i = 0; i < req.Pipelines.Count; i++)
        {
            var pipe = req.Pipelines[i];
            anchorOf[i] = pipe.Anchor is null
                ? pipe.Parts.Count - 1
                : pipe.Parts.Select(p => p.Name).ToList().IndexOf(pipe.Anchor);
            if (anchorOf[i] < 0)
                throw new InvalidOperationException($"{pipe.Suffix}: anchor '{pipe.Anchor}' is not a pool part");
        }

        var conversion = BindConversions(req, anchorOf);
        Matrix4x4? Conv(string dir) => conversion.TryGetValue(dir, out var d) ? d : null;
        var loadCache = new ConcurrentDictionary<string, StreamsLoad>(StringComparer.Ordinal);
        var unionInputCache = new ConcurrentDictionary<string, PoolMath.UnionInput>(StringComparer.Ordinal);
        StreamsLoad Load(string dir) => loadCache.GetOrAdd(dir, d => LoadStreams(d, Conv(d)));
        PoolMath.UnionInput UnionInput(string dir) => unionInputCache.GetOrAdd(dir, d => LoadUnionInput(d, Conv(d)));
        var partDirs = new Dictionary<string, string>(StringComparer.Ordinal);      // part/tier name → dump dir
        void ClaimName(string name, string dir)
        {
            if (partDirs.TryGetValue(name, out var prev))
            {
                if (!string.Equals(prev, dir, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"'{name}' appears in two pipelines with different dumps ('{prev}' vs '{dir}')");
            }
            else partDirs[name] = dir;
        }
        var opCache = new Dictionary<string, OperatorArt>(StringComparer.Ordinal);
        var slimParts = new HashSet<string>(StringComparer.Ordinal);   // parts/tiers whose operator shipped slim (Sel exists)
        // A dump's binds by bone hash, as the union reads them: the conversion is a property of the dump (one
        // dump, one space — BindConversions settles that), so every pipeline reads a shared dump the same way.
        var dumpBinds = new ConcurrentDictionary<string, Dictionary<uint, Matrix4x4>>(StringComparer.Ordinal);
        Dictionary<uint, Matrix4x4> BindsOf(string dir) => dumpBinds.GetOrAdd(dir,
            d => UnionInput(d).Binds.ToDictionary(kv => kv.Key, kv => BindSpace.FromRowMajor(kv.Value)));
        // Operators some pipeline binds AS SOLVED. A pipeline whose reference differs from a mesh's own binds
        // ships its own converted copy instead (see ShipOperator below), and a solved file no pipeline binds
        // is removed once every pipeline has spoken.
        var solvedOperatorUsers = new HashSet<string>(StringComparer.Ordinal);
        var convertedOperators = new HashSet<string>(StringComparer.Ordinal);
        var analysis = SolveOperators(req, Load, UnionInput, Conv, classificationOnly: true);
        OperatorArt Analysis(string name, string dir)
        {
            var solve = analysis[(name, dir)];
            solve.Error?.Throw();
            return solve.Art!;
        }
        var prunePlans = PlanPalettePruning(req, anchorOf, Load, UnionInput, analysis, diagnostics,
            out var demandedRows);
        var solved = SolveOperators(req, Load, UnionInput, Conv, demandedRows);
        OperatorArt Operator(string name, string dir)
        {
            if (opCache.TryGetValue(name, out var a))
            {
                if (a.Sel is not null) slimParts.Add(name);
                return a;
            }
            var solve = solved[(name, dir)];
            solve.Error?.Throw();      // the solve's own failure, at the point a serial build would hit it
            a = solve.Art!;
            diagnostics.AddRange(a.Diagnostics);
            File.WriteAllBytes(Path.Combine(req.OutDir, $"{name}_cpinv.buf"), FloatBytes(a.Cpinv));
            if (a.Sel is { } s && a.Off is { } o)
            {
                slimParts.Add(name);
                File.WriteAllBytes(Path.Combine(req.OutDir, $"{name}_sel.buf"), UIntBytes(s));
                File.WriteAllBytes(Path.Combine(req.OutDir, $"{name}_off.buf"), UIntBytes(o));
                File.WriteAllText(Path.Combine(req.OutDir, $"recover_{name}_cs.hlsl"),
                    ComputeTemplates.EmitRecover(4 * a.Hashes.Length));
            }
            else
            {
                File.WriteAllText(Path.Combine(req.OutDir, $"recover_{name}_cs.hlsl"),
                    ComputeTemplates.EmitRecoverDense(a.N, 4 * a.Hashes.Length));
            }
            return opCache[name] = a;
        }

        // copied texture files: one basename = one content source, loudly
        var copiedFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void CopyNamed(string src, string what)
        {
            string bn = Path.GetFileName(src);
            if (copiedFrom.TryGetValue(bn, out var prev))
            {
                if (!string.Equals(prev, src, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"{what} '{prev}' and '{src}' share the basename '{bn}'. Rename one");
                return;
            }
            copiedFrom[bn] = src;
            File.Copy(src, Path.Combine(req.OutDir, bn), overwrite: true);
        }

        // One replacement's authored per-submesh maps, taken the same way whichever route the replacement
        // goes down: the ini binds by basename, so every authored file is copied in and the slot rewritten
        // to it. drawCount is that replacement's own submesh count.
        SubmeshMaps?[] SubMapsFor(string sfx, IEnumerable<KeyValuePair<int, SubmeshMaps>> overrides, int drawCount)
        {
            MapSlot Ship(MapSlot slot)
            {
                if (slot.File is not { } src) return slot;
                CopyNamed(src, "textures");
                return MapSlot.From(Path.GetFileName(src));
            }
            var subMaps = new SubmeshMaps?[drawCount];
            foreach (var kv in overrides)
            {
                // refused before the range check: a row asking for something that cannot exist is a caller
                // fault whichever submesh it names, and skipping it would hide the fault behind a warning
                if (kv.Value.Albedo.IsNeutral)
                    throw new InvalidOperationException(
                        $"{sfx}: submesh {kv.Key} asks for a neutral base color. Only normal and RMO ship one");
                if (kv.Value.Ramp.IsNeutral)
                    throw new InvalidOperationException(
                        $"{sfx}: submesh {kv.Key} asks for a neutral ramp. Only normal and RMO ship one");
                if (kv.Key < 0 || kv.Key >= drawCount)
                {
                    // A diagnostic, not a warning: the row names this emitter's own pipeline suffix and a
                    // submesh position, and the change list refuses a texture set past the replacement's
                    // submesh count by name long before a build reaches here.
                    diagnostics.Add($"{sfx}: texture for submesh {kv.Key} is out of range ({drawCount} submeshes). Skipped");
                    continue;
                }
                subMaps[kv.Key] = new SubmeshMaps(Ship(kv.Value.Albedo), Ship(kv.Value.Normal),
                    Ship(kv.Value.Rmo), Ship(kv.Value.Ramp), Ship(kv.Value.Blend),
                    kv.Value.Properties?.Select(p => p with { Map = Ship(p.Map) }).ToList());
            }
            return subMaps;
        }

        var pipes = new List<PipelineEmission>();
        var palette = new Dictionary<string, PipelinePalette>(StringComparer.Ordinal);
        int ubTotal = 0, vcountTotal = 0;

        for (int pipeIdx = 0; pipeIdx < req.Pipelines.Count; pipeIdx++)
        {
            var pipe = req.Pipelines[pipeIdx];
            string sfx = pipe.Suffix;
            var parts = pipe.Parts.Select(p => p.Name).ToList();
            var dirs = pipe.Parts.Select(p => p.DumpDir).ToList();
            int anchorIdx = anchorOf[pipeIdx];
            var capHashes = pipe.CaptureHashes ?? new Dictionary<string, string>();
            var subTexOverrides = pipe.SubTextures ?? new Dictionary<int, SubmeshMaps>();

            // ---- union reconciliation (single union-order authority: first-seen across the pool) ------
            var unionInputs = dirs.Select(UnionInput).ToList();
            var rawUnion = PoolMath.BuildUnion(unionInputs);
            var plan = prunePlans[pipeIdx];
            var compact = PoolMath.CompactUnion(plan.FullUnion, plan.RetainedUnionRows);
            var union = compact.Union;
            int ub = union.UnionHashes.Length;
            ubTotal += ub;

            // ---- the bind each bone is STATED under ---------------------------------------------------
            // The game skins every mesh with that mesh's own binds, so the pool's meshes are free to bind
            // one bone differently — a whole mesh in another mesh space, a helper bone bound off the
            // skeleton, a tier re-bound after decimation. One donor needs one statement per bone, settled
            // by the build (ReferenceBinds). Every mesh this pipeline recovers rows from — pool parts, tiers,
            // wardrobe-group members — has the rows of each bone it binds differently converted onto that
            // statement, folded into its own copy of the mesh's operator. The fold sits AHEAD of everything
            // that reads a recovered row, which the witness convert requires: it solves a part's whole
            // draw-space relation from one shared bone, and a bind difference on that bone would ride into
            // every row the part owns. The reference arrives stated for the replaced part, in the space the
            // donor's vertices were compiled in, and the anchor's own restatement carries it to where the
            // dumps' binds are, as the donor compile carried it. Which part it was stated for makes no
            // difference here: each mesh is compared with it bone by bone.
            var anchorConversion = Conv(dirs[anchorIdx]);
            var reference = new Dictionary<uint, Matrix4x4>();
            foreach (var (h, bind) in pipe.ReferenceBinds ?? new Dictionary<uint, Matrix4x4>())
                reference[h] = anchorConversion is { } restate ? BindSpace.Rebase(bind, restate) : bind;
            var foldedOps = new HashSet<string>(StringComparer.Ordinal);
            // Ship one mesh's operator for THIS pipeline: as solved when the mesh states every retained bone
            // under the reference, else as a converted copy of its own.
            void ShipOperator(string name, string dir, OperatorArt art)
            {
                var hashes = UnionInput(dir).Hashes;
                var binds = BindsOf(dir);
                var constants = new double[]?[art.Hashes.Length];
                var converted = new List<(uint Hash, float Offset)>();
                for (int i = 0; i < art.Hashes.Length; i++)
                {
                    // a tied bone ships its TIE's rows, so it is the tie's statement that needs converting
                    int source = art.Weak[i] && art.TieFullRows[i] >= 0 ? art.TieFullRows[i] : art.SourceRows[i];
                    uint h = hashes[source];
                    if (!binds.TryGetValue(h, out var own) || !reference.TryGetValue(h, out var target)) continue;
                    if ((constants[i] = BindReference.Constant(target, own)) is null) continue;
                    converted.Add((art.Hashes[i], MathF.Max(RestBake.TranslationDiff(target, own),
                        RestBake.RotationDiff(target, own))));
                }
                if (converted.Count == 0) { solvedOperatorUsers.Add(name); return; }
                File.WriteAllBytes(Path.Combine(req.OutDir, $"{name}_cpinv_{sfx}.buf"),
                    FloatBytes(FoldConstants(art, constants)));
                foldedOps.Add(name);
                convertedOperators.Add(name);
                var worst = converted.OrderByDescending(c => c.Offset).ThenBy(c => c.Hash).Take(6)
                    .Select(c => (pipe.BonePaths is not null && pipe.BonePaths.TryGetValue(c.Hash, out var path)
                            ? BoneTable.MatchingSuffix(c.Hash, path) : null) is { } named
                        ? $"{named} (0x{c.Hash:x8}) {c.Offset:g3}" : $"0x{c.Hash:x8} {c.Offset:g3}");
                diagnostics.Add($"{sfx}: {name} binds {converted.Count} bone{(converted.Count == 1 ? "" : "s")} "
                    + "elsewhere than the replacement is posed from, so their recovered rows are converted "
                    + $"(largest offsets: {string.Join(", ", worst)})");
            }

            // ---- per-part shared operator + per-pipeline scatter map ----------------------------------
            // (map files are written AFTER witness selection below — witnesses repurpose entries)
            var partMeta = new List<(string Part, int N, int Nb, int Rows)>();
            var partScatter = new List<uint[]>();
            var partArts = new List<OperatorArt>();
            for (int i = 0; i < parts.Count; i++)
            {
                ClaimName(parts[i], dirs[i]);
                var load = Load(dirs[i]);
                bool active = plan.PoolSources.Contains((parts[i], dirs[i]));
                var art = active ? Operator(parts[i], dirs[i]) : Analysis(parts[i], dirs[i]);
                if (active) ShipOperator(parts[i], dirs[i], art);
                partArts.Add(art);
                var scatter = Enumerable.Repeat(PoolMath.Sentinel, art.Hashes.Length).ToArray();
                if (active)
                    for (int row = 0; row < art.Hashes.Length; row++)
                    {
                        int old = Array.IndexOf(plan.FullUnion.UnionHashes, art.Hashes[row]);
                        if (old >= 0 && plan.FullUnion.Owner[old] == i && compact.OldToCompact[old] >= 0)
                            scatter[row] = (uint)compact.OldToCompact[old];
                    }
                partScatter.Add(scatter);
                partMeta.Add((parts[i], load.P.GetLength(0), active ? art.Hashes.Length : 0,
                    active ? 4 * art.Hashes.Length : 0));

                int totalOwned = plan.FullUnion.Owner.Count(owner => owner == i);
                int retainedOwned = compact.SourceRows.Count(old => plan.FullUnion.Owner[old] == i);
                diagnostics.Add(retainedOwned == 0
                    ? $"palette: {sfx}/{parts[i]} 0/{totalOwned} rows used - ships nothing"
                    : $"palette: {sfx}/{parts[i]} {retainedOwned}/{totalOwned} rows used");
            }
            // Anchor-preferred ownership, applied before ANY consumer reads owner or scatter — the part
            // scatter maps, the owner buffer, the tier scatter and the witness reservations all see one
            // verdict. Needs the anchor's conditioning, which is why it waits for the operator loop.
            int movedRows = Enumerable.Range(0, rawUnion.Owner.Length)
                .Count(u => rawUnion.Owner[u] != anchorIdx && plan.FullUnion.Owner[u] == anchorIdx);
            if (movedRows > 0)
                diagnostics.Add($"{sfx}: {movedRows} union bone{(movedRows == 1 ? "" : "s")} re-owned to the "
                    + "anchor — recovered at its own draw instead of another part's");
            File.WriteAllBytes(Path.Combine(req.OutDir, $"owner_part_{sfx}.buf"),
                UIntBytes(union.Owner.Select(o => (uint)o).ToArray()));

            // ---- per-tier operators: same union and per-bone ownership as the part's lod0 -------------
            var tierMeta = new List<(string Part, string Name, string Suffix, string Hash, int Rows,
                DrawShapeSet? Shapes, TierMaterialMap? Map)>();
            var tierWork = new List<(string Name, int PartIdx, uint[] Scatter, OperatorArt Art)>();
            foreach (var t in pipe.Tiers ?? Array.Empty<PoolTier>())
            {
                int pi = parts.IndexOf(t.Part);
                if (pi < 0) throw new InvalidOperationException($"{sfx}: tier '{t.Name}': '{t.Part}' is not a pool part");
                string sourcePart = t.SourcePart ?? t.Part;
                string sourceTier = t.SourceMesh ?? t.Name;
                var verdicts = t.BoneVerdicts ?? Array.Empty<PoolDerive.TierBoneVerdict>();
                var consumedVerdicts = new HashSet<PoolDerive.TierBoneVerdict>();
                var mergedVerdicts = new List<PoolDerive.TierBoneVerdict>();
                ClaimName(t.Name, t.DumpDir);
                var load = Load(t.DumpDir);
                var tierHashes = UnionInput(t.DumpDir).Hashes;
                var scatter = new uint[load.Nb];
                var analysisArt = Analysis(t.Name, t.DumpDir);
                // the whole tier's per-bone weight in one traversal, on the first bone that needs it
                double[]? tierWeight = null;
                for (int b = 0; b < load.Nb; b++)
                {
                    int fullU = Array.IndexOf(plan.FullUnion.UnionHashes, tierHashes[b]);
                    if (fullU < 0)
                    {
                        if ((tierWeight ??= SummedWeights(load))[b] > 0)
                        {
                            // Gate 1 classified this exact weighted row. The emitter consumes that verdict
                            // and never tries to infer it again from the narrower emitted pool.
                            var matches = verdicts.Where(v => v.Bone == tierHashes[b]
                                && string.Equals(v.TierPart, sourcePart, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(v.Tier, sourceTier, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (matches.Count != 1)
                                throw new AuthoredRefusalException(
                                    $"LOD '{sourceTier}' of '{sourcePart}' cannot be built because its "
                                    + "geometry uses a bone missing from the original part. Internal "
                                    + "detail: expected exactly one upstream tier-row verdict but found "
                                    + $"{matches.Count}. Remove this mesh edit");
                            var verdict = matches[0];
                            if (verdict.Classification == PoolDerive.TierBoneClass.Merged
                                && verdict.OwningParts.Count == 0)
                                throw new AuthoredRefusalException(
                                    $"LOD '{sourceTier}' of '{sourcePart}' cannot be built because it is "
                                    + "missing geometry from another part at this detail level. Internal "
                                    + "detail: a MERGED tier-row verdict has no owning part. Remove this "
                                    + "mesh edit");
                            consumedVerdicts.Add(verdict);
                            if (verdict.Classification == PoolDerive.TierBoneClass.Merged)
                                mergedVerdicts.Add(verdict);
                        }
                        scatter[b] = PoolMath.Sentinel;
                        continue;
                    }
                    int u = compact.OldToCompact[fullU];
                    if (u < 0)
                    {
                        // This is a real pool row, but no shipped skin/witness context demands it. It is
                        // pruning, not an upstream tier-classification case.
                        scatter[b] = PoolMath.Sentinel;
                        continue;
                    }
                    // A tier is free to bind the bone elsewhere than its lod0 does — a decimated tier is often
                    // re-bound — because its rows are converted onto the pipeline's reference where they
                    // ship (ShipOperator below).
                    scatter[b] = union.Owner[u] == pi ? (uint)u : PoolMath.Sentinel;
                }
                if (consumedVerdicts.Count != verdicts.Count)
                    throw new AuthoredRefusalException(
                        $"LOD '{sourceTier}' of '{sourcePart}' cannot be built because its recorded "
                        + "bones do not match its geometry. Internal detail: an upstream tier-row verdict "
                        + "does not match a weighted bone outside the union. Remove this mesh edit");
                if (mergedVerdicts.Count > 0)
                {
                    var bones = new List<(uint Hash, string? Name)>();
                    foreach (var verdict in mergedVerdicts.OrderBy(v => v.Bone))
                    {
                        string? name = pipe.BonePaths is not null
                            && pipe.BonePaths.TryGetValue(verdict.Bone, out var fullPath)
                                ? BoneTable.MatchingSuffix(verdict.Bone, fullPath)
                                : null;
                        bones.Add((verdict.Bone, name));
                    }
                    mergedTierWarnings.Add(new MergedTierWarning(mergedVerdicts[0].AffectedPart, sourceTier,
                        mergedVerdicts.SelectMany(v => v.OwningParts)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), bones, t.PartDisplayNames));
                }
                // a decimated tier can leave an owned bone with degenerate weighted-vertex support — its
                // rows are tied to a sound co-riding bone (see BuildOperator). Only a bone with NO sound
                // stand-in falls back to the sentinel, keeping its lod0-recovered row — which lives in the
                // lod0 draw's space, so a same-frame two-placement context displaces it.
                for (int b = 0; b < load.Nb; b++)
                    if (scatter[b] != PoolMath.Sentinel && analysisArt.Weak[b] && analysisArt.TieFullRows[b] < 0)
                    {
                        scatter[b] = PoolMath.Sentinel;
                        diagnostics.Add($"{t.Name}: bone {BoneName(pipe.BonePaths, tierHashes[b])} has too little support in this "
                            + "lower-detail mesh to recover");
                    }
                // A bone this part owns that this tier's rig does not carry at all is written by nobody in
                // this tier's chain. Donor-weighted rows of that class are filled by the tier tie below
                // (see TierOrphanTies); a donor-unweighted one moves nothing and is only named here.
                {
                    var tierSet = new HashSet<uint>(tierHashes);
                    for (int u2 = 0; u2 < union.UnionHashes.Length; u2++)
                        if (union.Owner[u2] == pi && !tierSet.Contains(union.UnionHashes[u2]))
                            diagnostics.Add($"{t.Name}: this tier does not carry bone "
                                + $"{BoneName(pipe.BonePaths, union.UnionHashes[u2])}");
                }
                bool active = plan.TierSources.Contains((t.Name, t.DumpDir));
                if (active)
                {
                    var art = Operator(t.Name, t.DumpDir);
                    ShipOperator(t.Name, t.DumpDir, art);
                    var compactScatter = new uint[art.Hashes.Length];
                    for (int row = 0; row < art.Hashes.Length; row++)
                    {
                        int original = Array.IndexOf(tierHashes, art.Hashes[row]);
                        compactScatter[row] = original >= 0 ? scatter[original] : PoolMath.Sentinel;
                    }
                    tierWork.Add((t.Name, pi, compactScatter, art));
                    tierMeta.Add((t.Part, t.Name, t.Suffix, t.CaptureHash, 4 * art.Hashes.Length, t.Shapes, t.Map));
                }
                else
                    tierMeta.Add((t.Part, t.Name, t.Suffix, t.CaptureHash, 0, t.Shapes, t.Map));
            }

            // ---- witness bones: constants-free space conversion --------------------------------------
            // Per non-anchor part, pick a bone shared with the anchor and SOUND in every operator of both
            // (never weak/tied anywhere it appears). Both parts' recoveries of it are scattered into
            // reserved palette slots past the union; the witness convert reads them and solves
            // K = inv(M_w_part)·M_w_anchor per owned row. Draw constants play no role — some renderers
            // bind vs-cb1 as a window into a shared buffer that a whole-resource copy reads wrongly, and
            // per-part draw spaces genuinely differ (by up to ~150°).
            // LOD0 uses this route when every non-anchor OWNER has a sound witness. Tier chains continue to
            // use every witness available and pass an unwitnessed owner's rows through, as before. A one-part
            // pool designates no witness — no second draw space — and its anchor-owned rows pass through.
            uint nextSlot = (uint)ub;
            // build-log lines about the chain's conversion alone, withdrawn where the pipeline takes the
            // pose route, which converts nothing through witnesses or constants
            var chainOnly = new List<string>();
            var witRows = Enumerable.Repeat((PartRow: 0xFFFFFFFFu, AnchorRow: 0xFFFFFFFFu), parts.Count).ToArray();
            // The anchor's operators, and the slots its recoveries of a witness bone are reserved in. Both
            // the tier converts below and the group members further down solve K against the anchor's own
            // recovery of a shared bone, and a second reservation for one bone would leave one of them
            // reading a slot nothing writes. Anchor-preferred ownership makes the anchor-side reservation a
            // guard rather than a route: every selected witness bone is sound in the anchor's lod0 operator,
            // which is exactly the verdict the preference takes, so the bone's union row IS the anchor's.
            var anchorOps = new List<(uint[] Scatter, OperatorArt Art)> { (partScatter[anchorIdx], partArts[anchorIdx]) };
            anchorOps.AddRange(tierWork.Where(t => t.PartIdx == anchorIdx).Select(t => (t.Scatter, t.Art)));
            var anchorASlots = new Dictionary<uint, uint>();   // witness bone -> reserved anchor-side slot
            var lod0Owners = union.Owner.Where(pi => pi != anchorIdx).Distinct().ToList();

            bool Sound(OperatorArt art, uint h)
            {
                int idx = Array.IndexOf(art.Hashes, h);
                return idx >= 0 && !art.Weak[idx];
            }
            void Patch(uint[] scatter, OperatorArt art, uint h, uint slot)
            {
                int idx = Array.IndexOf(art.Hashes, h);
                if (idx >= 0) scatter[idx] = slot;
            }

            for (int pi = 0; pi < parts.Count; pi++)
            {
                if (pi == anchorIdx || !lod0Owners.Contains(pi)) continue;
                var partOps = new List<(uint[] Scatter, OperatorArt Art)> { (partScatter[pi], partArts[pi]) };
                partOps.AddRange(tierWork.Where(t => t.PartIdx == pi).Select(t => (t.Scatter, t.Art)));

                uint witness = 0;
                bool found = false;
                foreach (var h in partArts[anchorIdx].Hashes)
                    if (Array.IndexOf(union.UnionHashes, h) >= 0
                        && partOps.All(o => Sound(o.Art, h)) && anchorOps.All(o => Sound(o.Art, h)))
                    { witness = h; found = true; break; }
                if (!found)
                {
                    chainOnly.Add($"{sfx}: {parts[pi]} shares no sound bone with the anchor — its owned bones "
                        + "have no current-frame geometry conversion");
                    diagnostics.Add(chainOnly[^1]);
                    continue;
                }

                int realSlot = Array.IndexOf(union.UnionHashes, witness);
                bool partOwns = union.Owner[realSlot] == pi;
                bool anchorOwns = union.Owner[realSlot] == anchorIdx;
                uint partRow, anchorRow;
                if (partOwns) partRow = (uint)(realSlot * 4);
                else
                {
                    uint slot = nextSlot++;
                    foreach (var o in partOps) Patch(o.Scatter, o.Art, witness, slot);
                    partRow = slot * 4;
                }
                if (anchorOwns) anchorRow = (uint)(realSlot * 4);
                else
                {
                    if (!anchorASlots.TryGetValue(witness, out uint slot))
                    {
                        anchorASlots[witness] = slot = nextSlot++;
                        foreach (var o in anchorOps) Patch(o.Scatter, o.Art, witness, slot);
                    }
                    anchorRow = slot * 4;
                }
                witRows[pi] = (partRow, anchorRow);
            }

            bool lod0WitnessConvert = lod0Owners.All(pi => witRows[pi].PartRow != uint.MaxValue);
            if (!lod0WitnessConvert)
            {
                chainOnly.Add($"{sfx}: LOD0 has no complete current-frame witness conversion — it falls "
                    + "back to per-draw constants, whose freshness depends on draw order");
                diagnostics.Add(chainOnly[^1]);
            }
            if (lod0WitnessConvert || tierMeta.Count > 0)
            {
                File.WriteAllText(Path.Combine(req.OutDir, $"convert_witness_{sfx}.hlsl"),
                    ComputeTemplates.EmitConvertWitness(ub, anchorIdx, witRows.Select(w => (w.PartRow, w.AnchorRow)).ToList()));
            }

            // ---- wardrobe group slots: one APPENDED palette slot per group bone ------------------------
            // A group bone's rows are written at the MEMBER's own draw, not in the anchor's chain: exactly
            // one variant of a slot is worn and an unworn variant issues no draws, so whichever member drew
            // last wrote them. The region sits past the union AND the witness slots, and only in the
            // CONVERTED palette — both converts dispatch over union rows alone, so their copy round-trip
            // carries these through unchanged. Slots are handed out in Groups order, which is ascending slot
            // id then ascending hash, and that is the order the donor's own indices were compiled against.
            int originalGroupAt = 0;
            var groupSourceRows = new List<int>();
            var projectedGroups = new List<PoolGroup>();
            foreach (var original in pipe.Groups ?? Array.Empty<PoolGroup>())
            {
                var bones = new List<uint>();
                for (int i = 0; i < original.GroupBones.Count; i++)
                    if (plan.UsedGroupRows.Contains(originalGroupAt + i))
                    {
                        groupSourceRows.Add(originalGroupAt + i);
                        bones.Add(original.GroupBones[i]);
                    }
                originalGroupAt += original.GroupBones.Count;
                projectedGroups.Add(original with { GroupBones = bones });
            }
            var oldGroupToCompact = Enumerable.Repeat(-1, originalGroupAt).ToArray();
            for (int i = 0; i < groupSourceRows.Count; i++) oldGroupToCompact[groupSourceRows[i]] = i;
            var pipeGroups = (IReadOnlyList<PoolGroup>)projectedGroups;
            uint groupBase = nextSlot;
            int groupBoneCount = pipeGroups.Sum(g => g.GroupBones.Count);
            // The whole region is handed out BEFORE any member work, so it stays contiguous: a member's
            // witness reservation below takes a slot past it, and a region interleaved with those would put
            // the donor's compiled indices on the wrong rows.
            nextSlot += (uint)groupBoneCount;
            palette[sfx] = new PipelinePalette(plan.FullUnion.UnionHashes.Length,
                compact.SourceRows, groupBase, groupSourceRows);
            var groupSections = new List<GroupMemberEmission>();
            var groupClaims = new List<GroupMemberClaim>();
            uint regionAt = groupBase;
            foreach (var g in pipeGroups)
            {
                uint slotBase = regionAt;
                regionAt += (uint)g.GroupBones.Count;
                foreach (var member in g.Members)
                {
                    var meshes = member.Meshes ?? Array.Empty<PoolGroupMesh>();
                    // Recorded ahead of every verdict below: each of the three that drops a mesh would
                    // otherwise take a hidden member's suppression with it, and the mesh would draw
                    // normally with nothing saying so. The emission owes the skip to the MESH.
                    foreach (var m in meshes)
                        groupClaims.Add(new GroupMemberClaim(m.Name, m.CaptureHash, member.Hidden,
                            member.HiddenWhen));
                    if (g.GroupBones.Count == 0) continue;
                    var lod0 = meshes.FirstOrDefault(m => m.IsLod0);
                    if (lod0 is null)
                    {
                        diagnostics.Add($"{sfx}: group member '{member.Mesh}' carries no lod0 draw. It "
                            + "writes no rows, and the group's other members cover the bones while they are "
                            + "on screen");
                        continue;
                    }
                    var tiers = meshes.Where(m => !m.IsLod0
                        && plan.GroupSources.Contains((m.Name, m.DumpDir))).ToList();

                    // Per member, a witness bone for every fused section it emits — sound in the mesh's own
                    // operator AND in each of the anchor's, so both sides' recoveries of it are trustworthy.
                    // The dispatches run in the ANCHOR's chain, where a geometric K is the only one that
                    // cannot mix frames: the mesh's posed ref is current-frame there, but its constants copy
                    // is from its own last draw — pairing those would rebase this frame's geometry through
                    // last frame's transform. One witness for the lod0, one shared by the tiers (as the
                    // tier scatter machinery always required).
                    uint AnchorRowOf(uint bone)
                    {
                        int realSlot = Array.IndexOf(union.UnionHashes, bone);
                        if (union.Owner[realSlot] == anchorIdx) return (uint)(realSlot * 4);
                        if (!anchorASlots.TryGetValue(bone, out uint slot))
                        {
                            anchorASlots[bone] = slot = nextSlot++;
                            foreach (var o in anchorOps) Patch(o.Scatter, o.Art, bone, slot);
                        }
                        return slot * 4;
                    }
                    ClaimName(lod0.Name, lod0.DumpDir);
                    bool lod0Active = plan.GroupSources.Contains((lod0.Name, lod0.DumpDir));
                    var lod0Art = lod0Active ? Operator(lod0.Name, lod0.DumpDir) : Analysis(lod0.Name, lod0.DumpDir);
                    uint lod0Witness = 0, lod0WitnessRow = 0;
                    bool lod0HasWitness = false;
                    if (lod0Active)
                        foreach (var h in partArts[anchorIdx].Hashes)
                            if (Array.IndexOf(union.UnionHashes, h) >= 0
                                && Sound(lod0Art, h) && anchorOps.All(o => Sound(o.Art, h)))
                            { lod0Witness = h; lod0HasWitness = true; break; }
                    if (lod0HasWitness) lod0WitnessRow = AnchorRowOf(lod0Witness);
                    else
                        // The fallback keeps the capability at the cost the chain placement exists to
                        // remove: this one mesh's write order against the anchor's chain is whatever the
                        // draw stream decides.
                        diagnostics.Add($"{sfx}: group member '{member.Mesh}' lod0 shares no sound bone "
                            + "with the anchor — its rows rebase from draw constants at its own draw, and "
                            + "their write order against the anchor's chain follows the frame's draw order");
                    uint witness = 0, witnessAnchorRow = 0;
                    bool hasWitness = false;
                    if (tiers.Count > 0)
                    {
                        // Name-claimed here rather than only in the emit loop below: this pre-pass already
                        // mints the tier's operator files, and a tier the witness verdict then drops would
                        // never reach that loop — its files would land under a name nothing had claimed, out
                        // of reach of the same-name-different-dump refusal.
                        var tierArts = tiers.Select(t =>
                        {
                            ClaimName(t.Name, t.DumpDir);
                            return Operator(t.Name, t.DumpDir);
                        }).ToList();
                        foreach (var h in partArts[anchorIdx].Hashes)
                            if (Array.IndexOf(union.UnionHashes, h) >= 0
                                && tierArts.All(a => Sound(a, h)) && anchorOps.All(o => Sound(o.Art, h)))
                            { witness = h; hasWitness = true; break; }
                        if (!hasWitness)
                        {
                            diagnostics.Add($"{sfx}: group member '{member.Mesh}' shares no sound bone with "
                                + "the anchor, so its other LOD tiers write no rows. Its lod0 draw still does");
                            tiers.Clear();
                        }
                        else witnessAnchorRow = AnchorRowOf(witness);
                    }

                    string? lod0Emitted = null;   // the lod0's emission name, once it ships an IN-CHAIN section
                    foreach (var mesh in tiers.Prepend(lod0))
                    {
                        if (!plan.GroupSources.Contains((mesh.Name, mesh.DumpDir))) continue;
                        ClaimName(mesh.Name, mesh.DumpDir);
                        var art = Operator(mesh.Name, mesh.DumpDir);
                        // This member's local bone per group bone, or the recover shaders' own "write
                        // nothing" sentinel. A bone the mesh cannot condition is NOT tied rigidly to a
                        // neighbour here: a tie is sound for geometry that RIDES the bone, and nothing of
                        // this member's rides the donor's vertices — the row would simply be wrong.
                        var gmap = new uint[g.GroupBones.Count];
                        for (int k = 0; k < g.GroupBones.Count; k++)
                        {
                            int idx = Array.IndexOf(art.Hashes, g.GroupBones[k]);
                            if (idx < 0)
                            {
                                gmap[k] = PoolMath.Sentinel;
                                diagnostics.Add($"{sfx}: {mesh.Name} does not carry bone {BoneName(pipe.BonePaths, g.GroupBones[k])}, "
                                    + "so it writes no rows for it");
                            }
                            else if (art.Weak[idx])
                            {
                                gmap[k] = PoolMath.Sentinel;
                                diagnostics.Add($"{sfx}: {mesh.Name} recovers bone {BoneName(pipe.BonePaths, g.GroupBones[k])} "
                                    + "ill-conditioned, so it writes no rows for it");
                            }
                            else gmap[k] = (uint)idx;
                        }
                        if (gmap.All(v => v == PoolMath.Sentinel)) continue;   // nothing left for it to write
                        ShipOperator(mesh.Name, mesh.DumpDir, art);
                        File.WriteAllBytes(Path.Combine(req.OutDir, $"{mesh.Name}_gmap_{sfx}.buf"), UIntBytes(gmap));
                        bool slim = slimParts.Contains(mesh.Name);
                        bool atDraw = mesh.IsLod0 && !lod0HasWitness;
                        File.WriteAllText(Path.Combine(req.OutDir, $"grpfuse_{mesh.Name}_{sfx}.hlsl"),
                            atDraw
                                ? ComputeTemplates.EmitGroupFuse(g.GroupBones.Count, (int)slotBase, slim, art.N)
                                : mesh.IsLod0
                                    ? ComputeTemplates.EmitGroupFuseWitness(g.GroupBones.Count, (int)slotBase, slim,
                                        art.N, Array.IndexOf(art.Hashes, lod0Witness), lod0WitnessRow)
                                    : ComputeTemplates.EmitGroupFuseWitness(g.GroupBones.Count, (int)slotBase, slim,
                                        art.N, Array.IndexOf(art.Hashes, witness), witnessAnchorRow));
                        if (mesh.IsLod0 && !atDraw) lod0Emitted = mesh.Name;
                        groupSections.Add(new GroupMemberEmission(mesh.Name, mesh.CaptureHash, mesh.IsLod0,
                            g.GroupBones.Count, atDraw, mesh.IsLod0 ? null : lod0Emitted));
                    }
                }
            }

            // ---- tier ties: donor-weighted rows a tier chain writes nothing for -----------------------
            // Computed after the witness reservations so a row redirected to a reserved slot is never a
            // copy source. One shader per LOD level (suffix): the anchor's chain at that level runs every
            // part's same-level tier, so the level's orphan rows across parts fill together.
            var tierTiePairs = new Dictionary<string, List<(uint Tied, uint Source)>>(StringComparer.Ordinal);
            var tierOrphans = new Dictionary<string, List<(uint Tied, uint Source)>>(StringComparer.Ordinal);
            {
                var donorRows = new HashSet<int>();
                foreach (int old in plan.SkinUnionRows)
                    if (compact.OldToCompact[old] >= 0) donorRows.Add(compact.OldToCompact[old]);
                foreach (var (name, pi, scatter, _) in tierWork)
                {
                    var tier = (pipe.Tiers ?? Array.Empty<PoolTier>()).First(x => x.Name == name);
                    var pairs = TierOrphanTies(name, scatter, union, pi, donorRows,
                        Load(pipe.Parts[pi].DumpDir), unionInputs[pi].Hashes, pipe.BonePaths, diagnostics);
                    if (pairs.Count == 0) continue;
                    tierOrphans[name] = pairs;
                    if (!tierTiePairs.TryGetValue(tier.Suffix, out var list))
                        tierTiePairs[tier.Suffix] = list = new List<(uint, uint)>();
                    list.AddRange(pairs);
                    string display = tier.PartDisplayNames is not null
                        && tier.PartDisplayNames.TryGetValue(tier.Part, out var shown) ? shown : tier.Part;
                    warnings.Add($"'{display}' moves less naturally at longer view distances: its "
                        + $"lower-detail mesh does not use {pairs.Count} bone{(pairs.Count == 1 ? "" : "s")} "
                        + "the replacement mesh uses. The build log names the bones.");
                }
            }
            var tierTies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (suffix, pairs) in tierTiePairs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var ordered = pairs.OrderBy(p => p.Tied).ToList();
                File.WriteAllText(Path.Combine(req.OutDir, $"tiertie_{suffix}_{sfx}.hlsl"),
                    ComputeTemplates.EmitTierTieFill(ordered));
                tierTies[suffix] = ordered.Count;
            }

            if (partMeta[anchorIdx].Rows == 0
                && (union.Owner.Any(owner => owner != anchorIdx) || groupSections.Any(m => m.AtDraw)))
                throw new InvalidOperationException($"{sfx}: live recovery requires the anchor's draw-space "
                    + "constants, but the anchor supplies no retained palette row. The build cannot remove "
                    + "its capture without degrading the replacement");

            for (int i = 0; i < parts.Count; i++)
                if (partMeta[i].Rows > 0)
                    File.WriteAllBytes(Path.Combine(req.OutDir, $"{parts[i]}_map_{sfx}.buf"), UIntBytes(partScatter[i]));
            foreach (var (name, _, scatter, _) in tierWork)
                File.WriteAllBytes(Path.Combine(req.OutDir, $"{name}_map_{sfx}.buf"), UIntBytes(scatter));

            // union palette SEED = identity per bone, witness and wardrobe-group slots included. Both
            // palettes are seeded from this one file, so the appended region exists in the converted one
            // the member dispatches write into.
            var ident = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            var identBytes = FloatBytes(ident);
            var seed = new byte[identBytes.Length * (int)nextSlot];
            for (int u = 0; u < (int)nextSlot; u++) Buffer.BlockCopy(identBytes, 0, seed, u * identBytes.Length, identBytes.Length);
            File.WriteAllBytes(Path.Combine(req.OutDir, $"palette_seed_{sfx}.buf"), seed);

            for (int chunk = 0; chunk < ComputeTemplates.ConvertChunks(parts.Count); chunk++)
                File.WriteAllText(Path.Combine(req.OutDir, ConvertFile(sfx, chunk)),
                    ComputeTemplates.EmitConvert(parts.Count, ub, chunk));

            // ---- the new geometry: donor streams, or the identity concat of the pool parts -----------
            int vcount, vb1Stride;
            List<PoolMath.Submesh> submeshes;
            string ibFmt;
            // Identity builds carry no ties — NOT because absence is harmless there (the identity body
            // concatenates every part, so an absent part's region is on screen and its rows collapse the
            // same way), but because the route ships from no app build (ModBuilder always sets DonorDir)
            // and carries no bone paths to tie with. Emitter-API/test reach only; recorded, not cured.
            var ties = new List<(string Part, int Pairs)>();
            var tiePairs = new List<(uint Tied, uint Source)>();
            if (pipe.DonorDir is null)
            {
                var idParts = dirs.Select(d => LoadIdentityPart(d, Conv(d))).ToList();
                var body = PoolMath.BuildIdentityBody(idParts, union.FullMaps);
                File.WriteAllBytes(Path.Combine(req.OutDir, $"combined_bind_{sfx}.buf"), body.Bind);
                File.WriteAllBytes(Path.Combine(req.OutDir, $"combined_vb1_{sfx}.buf"), body.Vb1);
                File.WriteAllBytes(Path.Combine(req.OutDir, $"combined_skin_{sfx}.buf"), body.Skin);
                File.WriteAllBytes(Path.Combine(req.OutDir, $"combined_ib_{sfx}.buf"), body.Ib);
                vcount = body.Verts;
                vb1Stride = body.Vb1Stride;
                submeshes = body.Submeshes.ToList();
                ibFmt = "DXGI_FORMAT_R16_UINT";
                File.WriteAllText(Path.Combine(req.OutDir, $"combined_meta_{sfx}.json"),
                    CombinedMetaJson(vcount, vb1Stride, submeshes));
            }
            else
            {
                foreach (var (src, dst) in new[]
                         {
                             ("stream0.buf", $"combined_bind_{sfx}.buf"), ("stream1.buf", $"combined_vb1_{sfx}.buf"),
                             ("ib.buf", $"combined_ib_{sfx}.buf"),
                         })
                    File.WriteAllBytes(Path.Combine(req.OutDir, dst), File.ReadAllBytes(Path.Combine(pipe.DonorDir, src)));

                // The donor compiles its group-bone weights onto a DENSE continuation of the union
                // (unionBones + k). The witness slots sit between the two in the palette, and only the
                // emission knows how many it reserved, so the offset is added here — at the one write site —
                // rather than guessed at compile time.
                var skinStream = File.ReadAllBytes(Path.Combine(pipe.DonorDir, "stream2.buf"));
                RemapSkinIndices(skinStream, plan.FullUnion.UnionHashes.Length, compact.OldToCompact,
                    groupBase, oldGroupToCompact, sfx);
                File.WriteAllBytes(Path.Combine(req.OutDir, $"combined_skin_{sfx}.buf"), skinStream);

                using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(pipe.DonorDir, "meta.json")));
                var root = meta.RootElement;
                vcount = root.GetProperty("verts").GetInt32();
                var s1 = File.ReadAllBytes(Path.Combine(pipe.DonorDir, "stream1.buf"));
                vb1Stride = vcount != 0 ? s1.Length / vcount : 20;
                submeshes = new List<PoolMath.Submesh>();
                if (root.TryGetProperty("submeshes", out var sm) && sm.ValueKind == JsonValueKind.Array && sm.GetArrayLength() > 0)
                    foreach (var e in sm.EnumerateArray())
                        submeshes.Add(new PoolMath.Submesh(e.GetProperty("firstByte").GetInt32(),
                            e.GetProperty("indexCount").GetInt32(), e.GetProperty("baseVertex").GetInt32()));
                else
                    submeshes.Add(new PoolMath.Submesh(0,
                        File.ReadAllBytes(Path.Combine(pipe.DonorDir, "ib.buf")).Length / 2, 0));
                string idxFmt = root.TryGetProperty("indexFormat", out var ifmt) ? (ifmt.GetString() ?? "") : "";
                ibFmt = idxFmt.Contains("R32") ? "DXGI_FORMAT_R32_UINT" : "DXGI_FORMAT_R16_UINT";

                var (newW, newBi) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(pipe.DonorDir, "stream2.buf")));
                int maxBi = -1;
                for (int i = 0; i < newBi.GetLength(0); i++)
                    for (int k = 0; k < 4; k++) maxBi = Math.Max(maxBi, newBi[i, k]);
                // The donor's own index space is the union followed by the group bones, so that is what the
                // bound is taken against; the palette offset the write above applied is a later step.
                int donorBones = plan.FullUnion.UnionHashes.Length + oldGroupToCompact.Length;
                // A diagnostic, not a warning: every word of it — palette row, union, "recompile the donor"
                // — is this emitter's own account of its own compile, and none of it names anything the
                // modder made or can act on.
                if (newBi.Length > 0 && maxBi >= donorBones)
                    diagnostics.Add(groupBoneCount == 0
                        ? $"{sfx}: new geometry references union bone {maxBi} but the union has "
                          + $"{plan.FullUnion.UnionHashes.Length} (0..{plan.FullUnion.UnionHashes.Length - 1}). " +
                          "Recompile the donor against THIS union."
                        : $"{sfx}: new geometry references bone {maxBi} but the union and its wardrobe slots have " +
                          $"{donorBones} (0..{donorBones - 1}). Recompile the donor against THIS union.");

                // ---- tie underlay: a donor-used union bone another part owns rides its nearest
                // anchor-owned ancestor while that part's presence latch is down. Weighted use only — a
                // slot the donor merely indexes at zero weight moves no vertex and earns no tie.
                var donorSlots = new HashSet<int>();
                foreach (int old in plan.SkinUnionRows)
                    if (compact.OldToCompact[old] >= 0) donorSlots.Add(compact.OldToCompact[old]);
                ties = TieUnderlay(req.OutDir, sfx, union, anchorIdx, parts, donorSlots,
                    pipe.BonePaths, diagnostics, tiePairs);
            }
            vcountTotal += vcount;

            // ---- stream-1 variants: the donor draw at an anchor tier is read through THAT tier mesh's
            // input layout, and a tier can store stream 1 differently from the lod0 the stream was built
            // for (float UVs beside half UVs) ---------------------------------------------------------
            var donorLayout = MetaChannels.Read(pipe.DonorDir ?? dirs[anchorIdx]);
            var anchorTiers = (pipe.Tiers ?? Array.Empty<PoolTier>()).Where(t => t.Part == parts[anchorIdx])
                .Select(t => new TierLayout(t.Name, t.SourceMesh ?? t.Name, t.SourcePart ?? t.Part,
                    MetaChannels.Read(t.DumpDir))).ToList();
            // stream 0 is posed into one 40-byte shape every frame, so a tier reading it otherwise has no
            // copy to be given
            StreamVariants(req.OutDir, $"combined_bind_{sfx}", vcount, donorLayout, stream: 0, anchorTiers,
                diagnostics, reencode: false);
            var (vb1Variants, tierVb1) = StreamVariants(req.OutDir, $"combined_vb1_{sfx}", vcount, donorLayout,
                stream: 1, anchorTiers, diagnostics);

            // shipped compact palette layout record (donors compile against the full union, not this order)
            File.WriteAllText(Path.Combine(req.OutDir, $"union_{sfx}.json"),
                UnionJson(ub, union.UnionHashes, partMeta));

            int bpi = ibFmt.Contains("R16") ? 2 : 4;
            var draws = submeshes.Select(s => (Count: s.IndexCount, Start: s.FirstByte / bpi, Base: s.BaseVertex)).ToList();

            // ---- the per-copy pose route: a pipeline recovering every row from the replaced part itself
            // skins each copy of that part from that copy's own draw, in pixel passes before each draw -----
            // ---- a pool reaching other parts takes the same route where every part it takes rows from is
            // matched to each copy by placement (see PlanPalettePruning) --------------------------------
            int UnionSlot(uint hash) => compact.OldToCompact[Array.IndexOf(plan.FullUnion.UnionHashes, hash)];
            Vector3 RestOrigin(string dir, uint hash) => Matrix4x4.Invert(BindsOf(dir)[hash], out var restWorld)
                ? restWorld.Translation
                : throw new InvalidDataException($"{sfx}: bone {BoneName(pipe.BonePaths, hash)} of '{dir}' has a bind that cannot be inverted");
            var copyCache = PoseRouteFor(req.OutDir, sfx, partMeta, anchorIdx, partArts,
                tierWork, tierMeta, groupSections, ties, tiePairs, tierOrphans, plan.Pooled, UnionSlot, RestOrigin,
                pipe.BonePaths, (int)nextSlot, vcount, vb1Stride, vb1Variants, ibFmt, draws, PoseWindowVertices, diagnostics);
            if (copyCache is not null)
                // The pose route converts nothing through witnesses or constants. The witness rows the plan
                // kept stay in the operators, which were solved with them (dropping them now would take a
                // second solve), and the lines saying why they were kept are withdrawn.
                foreach (string line in chainOnly.Concat(plan.WitnessLines)) diagnostics.RemoveAt(diagnostics.LastIndexOf(line));
            else if (plan.Pooled is not null)
                // The chain stays after all (no vertices, a palette too wide). The rows the plan kept to place
                // parts stay retained for the same reason, and place nothing, so the lines saying why go.
                foreach (string line in plan.PlacementLines) diagnostics.RemoveAt(diagnostics.LastIndexOf(line));
            if (copyCache is null)
                File.WriteAllText(Path.Combine(req.OutDir, $"skin_cs_{sfx}.hlsl"), ComputeTemplates.EmitSkin(vcount));
            else
                // the chain's own files, written above before the shape was settled, are read by nothing
                foreach (string stale in new[] { $"palette_seed_{sfx}.buf", $"owner_part_{sfx}.buf", $"convert_witness_{sfx}.hlsl" }
                             .Concat(Enumerable.Range(0, ComputeTemplates.ConvertChunks(parts.Count)).Select(c => ConvertFile(sfx, c)))
                             .Concat(tierTies.Keys.Select(suffix => $"tiertie_{suffix}_{sfx}.hlsl"))
                             .Concat(ties.Select(tie => $"tiefill_{tie.Part}_{sfx}.hlsl")))
                    File.Delete(Path.Combine(req.OutDir, stale));

            // ---- per-submesh maps ---------------------------------------------------------------------
            var subMaps = SubMapsFor(sfx, subTexOverrides, draws.Count);

            pipes.Add(new PipelineEmission(sfx, partMeta, anchorIdx, capHashes, ub, vcount, vb1Stride,
                ibFmt, draws, subMaps,
                pipe.NoSkipParts is { Count: > 0 } ns ? new HashSet<string>(ns, StringComparer.Ordinal) : null,
                tierMeta, lod0WitnessConvert, pipe.ToggleKey, pipe.Latch, pipe.HideWhenOff, pipe.HiddenBy,
                pipe.ShownBy, pipe.SuppressWhen,
                groupSections, groupClaims, ties, tierTies,
                pipe.PresenceHashes, pipe.AnchorShapes, vb1Variants, tierVb1, foldedOps, copyCache));
        }

        // every mesh's gather draws through the one pixel shader, and every pose pass through the one
        // fullscreen vertex shader
        if (pipes.Any(p => p.PoseRoute is not null))
        {
            File.WriteAllText(Path.Combine(req.OutDir, GatherPixelFile), ComputeTemplates.EmitGatherPixel());
            File.WriteAllText(Path.Combine(req.OutDir, PoseFullscreenFile), ComputeTemplates.EmitPoseFullscreen());
        }
        // the pooled route's capture and frame passes, one shader of each per mod, and the ring slots' numbers;
        // each source mesh's ring gather and stamp ship beside its packet layout above
        if (pipes.Any(p => p.PoseRoute?.Sources is not null))
        {
            File.WriteAllText(Path.Combine(req.OutDir, PoseCaptureVsFile), ComputeTemplates.EmitPoseCaptureVertex());
            File.WriteAllText(Path.Combine(req.OutDir, PoseCapturePsFile), ComputeTemplates.EmitPoseCapturePixel());
            File.WriteAllText(Path.Combine(req.OutDir, PoseFrameFile), ComputeTemplates.EmitPoseFrame(FrameWrap));
            for (int k = 0; k < PoseRingEntries; k++)
                File.WriteAllBytes(Path.Combine(req.OutDir, RingSlotFile(k)), UIntBytes(new[] { (uint)k }));
        }

        // A solved operator file every one of its pipelines replaced with a converted copy is bound by
        // nothing, so it does not ship.
        foreach (string name in convertedOperators)
            if (!solvedOperatorUsers.Contains(name))
                File.Delete(Path.Combine(req.OutDir, $"{name}_cpinv.buf"));

        // ---- rigid replacements: the compiled donor streams, shipped and drawn as they are ---------------
        // No capture, no palette, no compute: the streams are already in the replaced part's own layout, so
        // the section swaps the buffers under its draw and reissues it.
        var rigids = new List<RigidEmission>();
        foreach (var r in reqRigids)
        {
            string sfx = r.Suffix;
            var streams = new List<(int Stream, int Stride)>();
            using (var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.DonorDir, "meta.json"))))
            {
                var root = meta.RootElement;
                int vcount = root.GetProperty("verts").GetInt32();
                vcountTotal += vcount;
                foreach (var e in root.GetProperty("streams").EnumerateArray())
                    streams.Add((e.GetProperty("stream").GetInt32(), e.GetProperty("stride").GetInt32()));
                string idxFmt = root.TryGetProperty("indexFormat", out var ifmt) ? (ifmt.GetString() ?? "") : "";
                string ibFmt = idxFmt.Contains("R32") ? "DXGI_FORMAT_R32_UINT" : "DXGI_FORMAT_R16_UINT";
                int bpi = ibFmt.Contains("R16") ? 2 : 4;

                // vb0 (position) and vb1 (colour/uv) are all a rigid target has: this route takes only
                // meshes storing no per-vertex influences, so there is no skin stream to ship.
                File.WriteAllBytes(Path.Combine(req.OutDir, $"rigid_vb0_{sfx}.buf"),
                    File.ReadAllBytes(Path.Combine(r.DonorDir, "stream0.buf")));
                string vb1 = Path.Combine(r.DonorDir, "stream1.buf");
                bool hasVb1 = File.Exists(vb1);
                if (hasVb1) File.WriteAllBytes(Path.Combine(req.OutDir, $"rigid_vb1_{sfx}.buf"), File.ReadAllBytes(vb1));
                File.WriteAllBytes(Path.Combine(req.OutDir, $"rigid_ib_{sfx}.buf"),
                    File.ReadAllBytes(Path.Combine(r.DonorDir, "ib.buf")));

                var draws = new List<(int Count, int Start, int Base)>();
                if (root.TryGetProperty("submeshes", out var sm) && sm.ValueKind == JsonValueKind.Array
                    && sm.GetArrayLength() > 0)
                    foreach (var e in sm.EnumerateArray())
                        draws.Add((e.GetProperty("indexCount").GetInt32(),
                            e.GetProperty("firstByte").GetInt32() / bpi, e.GetProperty("baseVertex").GetInt32()));
                else
                    draws.Add((File.ReadAllBytes(Path.Combine(r.DonorDir, "ib.buf")).Length / bpi, 0, 0));

                var subMaps = SubMapsFor(sfx, r.SubTextures ?? new Dictionary<int, SubmeshMaps>(), draws.Count);

                // the tiers' draws read these streams through their OWN meshes' layouts
                var builtFor = MetaChannels.Read(r.DonorDir);
                var tierLayouts = (r.TierHashes ?? Array.Empty<string>()).Select(h =>
                    r.TierLayouts is not null && r.TierLayouts.TryGetValue(h, out var tl)
                        ? new TierLayout(h, tl.Mesh, null, tl.Channels)
                        : new TierLayout(h, h, null, null)).ToList();
                var (vb0Variants, tierVb0) = StreamVariants(req.OutDir, $"rigid_vb0_{sfx}", vcount, builtFor,
                    stream: 0, tierLayouts, diagnostics);
                var (vb1Variants, tierVb1) = StreamVariants(req.OutDir, $"rigid_vb1_{sfx}", vcount, builtFor,
                    stream: 1, tierLayouts, diagnostics);

                rigids.Add(new RigidEmission(sfx, r.Hashes.ToList(),
                    streams.FirstOrDefault(s => s.Stream == 0).Stride,
                    hasVb1 ? streams.FirstOrDefault(s => s.Stream == 1).Stride : null,
                    ibFmt, draws, subMaps, r.ToggleKey, r.Latch, r.HideWhenOff, r.HiddenBy, r.ShownBy,
                    r.SuppressWhen, r.ShapesByHash, r.MapsByHash, vb0Variants, tierVb0, vb1Variants, tierVb1, r.Part ?? sfx));
            }
        }

        // neutral data maps (UNORM — sampled linearly), shipped only for the kind some submesh asks for.
        // Stomping a slot whose per-pass meaning is unknown paints with the neutral's raw colour, so a
        // slot nobody asked to blank keeps the anchor's real map through the save/restore.
        var allSubMaps = pipes.Select(p => p.SubMaps).Concat(rigids.Select(r => r.SubMaps)).ToList();
        if (allSubMaps.Any(m => UsesNeutral(m, StockMapKind.Normal)))
            FlatDds.Write(Path.Combine(req.OutDir, "neutral_n.dds"), (128, 128, 255, 255), srgb: false);
        if (allSubMaps.Any(m => UsesNeutral(m, StockMapKind.Rmo)))
            FlatDds.Write(Path.Combine(req.OutDir, "neutral_rmo.dds"), (128, 0, 255, 0), srgb: false);

        // stock-map slot tags: global sections keyed by texture hash, deduped across replacements. A
        // hash claimed as two different kinds can only mis-probe — keep the first, and say so.
        var slotTags = new List<StockMapTag>();
        var tagKinds = new Dictionary<string, StockMapKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var tags in req.Pipelines.Select(p => p.StockMaps)
                     .Concat(reqRigids.Select(r => r.StockMaps)))
            foreach (var t in tags ?? Array.Empty<StockMapTag>())
            {
                if (tagKinds.TryGetValue(t.Hash, out var kind))
                {
                    if (kind != t.Kind)
                        diagnostics.Add($"stock texture {t.Hash} is tagged both {kind} and {t.Kind}; keeping {kind}");
                    continue;
                }
                tagKinds[t.Hash] = t.Kind;
                slotTags.Add(t);
            }
        var propertyTags = req.Pipelines.SelectMany(p => p.StockProperties ?? Array.Empty<StockPropertyTag>())
            .Concat(reqRigids.SelectMany(r => r.StockProperties ?? Array.Empty<StockPropertyTag>()))
            .GroupBy(t => $"{t.Hash}\u001f{t.ShaderProperty}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(t => t.ShaderProperty, StringComparer.Ordinal)
            .ThenBy(t => t.Hash, StringComparer.OrdinalIgnoreCase).ToList();
        // ---- the ini --------------------------------------------------------------------------------
        // Section ownership is settled before a byte is written: each ib hash gets ONE OWNING
        // TextureOverride (the runtime runs every section whose match filters pass, in name order at
        // equal priority — deliberate same-hash companions extend the owner's name; an unintended
        // second section would act on the same draws), so sighting assignments and scoped binds land
        // INSIDE the owning section. The retexture text is composed first — it hands its same-hash
        // bind blocks to the capture units — and appended after the pooled emission.
        // one guard per guarded hash settles here, so the tag sections, the declarations, the collision
        // walk and the emission all read the same dictionary
        var guards = GuardsByHash(req.TwinGuards);
        RefuseTagCollisions(slotTags.Select(t => (Hash: t.Hash, Part: t.Part))
                .Concat(propertyTags.Select(t => (t.Hash, t.Part))),
            (req.ScopedRetextures ?? Array.Empty<ScopedRetexEntry>())
                .Select(e => (Hash: e.StockHash, Part: e.Part)),
            MintedProbeTagHashes(guards.Values, req.StockRamps, req.StockDraws)
                .Select(h => (Hash: h, Part: "")),
            BufferTags(req.MeshLabels, req.Pipelines
                .SelectMany(p => p.CaptureHashes?.Values ?? Enumerable.Empty<string>())
                // a pool's lod0 captures, its tiers' captures and every group member's captured draws all
                // open sections of their own, so every one of their keys is walked
                .Concat(req.Pipelines.SelectMany(p => (p.Tiers ?? Array.Empty<PoolTier>()).Select(t => t.CaptureHash)))
                .Concat(req.Pipelines.SelectMany(p => (p.Groups ?? Array.Empty<PoolGroup>())
                    .SelectMany(g => g.Members).SelectMany(m => m.Meshes ?? Array.Empty<PoolGroupMesh>())
                    .Select(m => m.CaptureHash)))
                .Concat(reqRigids.SelectMany(r => new[] { r.Hash }.Concat(r.TierHashes ?? Array.Empty<string>())))
                .Concat(req.HideHashes ?? Array.Empty<string>())
                .Concat((req.Latches ?? Array.Empty<WitnessLatch>()).SelectMany(l => l.WitnessIbs))
                .Concat((req.ScopedRetextures ?? Array.Empty<ScopedRetexEntry>())
                    .SelectMany(e => e.Images.SelectMany(i => i.Anchors.Select(a => a.Hash))))
                .Concat((req.StockRamps ?? Array.Empty<StockRampBind>()).Select(b => b.IbHash))
                .Concat((req.StockDraws ?? Array.Empty<StockDrawSite>()).Select(s => s.IbHash))
                .Concat((req.TwinSightings ?? Array.Empty<TwinSighting>()).Select(t => t.Hash))));
        var hides = (req.HideHashes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        var units = BuildCaptureUnits(pipes, req.ToggleKey, guards, req.HiddenFlags);
        foreach (var h in hides)
            if (units.ByHash.ContainsKey(h))
                throw new InvalidOperationException(
                    $"hide hash {h} is also a pipeline capture hash — the capture's skip already covers it");
        // A rigid replacement owns a section per hash it draws at, so no other section may claim one:
        // the runtime runs every match-passing section on a hash, so two claimants would both skip and
        // both draw at the same fires — double suppression and a double donor draw, never intended.
        var hideSet = new HashSet<string>(hides, StringComparer.Ordinal);
        var rigidOwner = new Dictionary<string, RigidEmission>(StringComparer.Ordinal);
        foreach (var r in rigids)
            foreach (var h in r.Hashes)
            {
                if (units.ByHash.ContainsKey(h))
                    throw new InvalidOperationException(
                        $"'{r.Sfx}' replaces draw {h}, which a pooled pipeline also captures. "
                        + "The two can't share one section, so this mod can't be built");
                if (hideSet.Contains(h))
                    throw new InvalidOperationException(
                        $"'{r.Sfx}' replaces draw {h}, which is also hidden. "
                        + "The replacement's own suppression already covers it");
                if (rigidOwner.TryGetValue(h, out var owner))
                    throw new InvalidOperationException(
                        $"'{owner.Sfx}' and '{r.Sfx}' replace one draw signature. The swap can't tell them "
                        + "apart, so this mod can't be built");
                rigidOwner[h] = r;
            }
        // Presence latches. Group members latch PER MESH (each fused dispatch reads its own mesh's
        // buffer). Pool parts latch PER PART — one latch sighted by the part's lod0, every tier, and any
        // dropped-tier hash the builder recorded (a dropped tier's vanilla draw still proves the part is
        // on screen): the part's recovers gate on it and the tie underlay fires on its exact complement,
        // so no state runs neither. A recover admitted by a tier's sighting reads the part's last
        // captured lod0 pair (posed ref + CB copy, both from its last lod0 draw — a consistent stale
        // frame, today's off-screen class). [Present] commits every latch; chains test last frame's
        // verdict. The anchor needs none: the chain firing IS its draw.
        var meshLatches = pipes.SelectMany(p =>
            {
                // the pooled route reads no latch: a part with no copy found at a draw takes the tie rows
                // at that draw
                if (p.PoseRoute?.Sources is not null) return Enumerable.Empty<(string Name, string Hash)>();
                string anchor = p.PartMeta[p.AnchorIdx].Part;
                return p.GroupMembers.Where(m => !m.AtDraw).Select(m => (m.Name, m.Hash))
                    .Concat(p.PartMeta
                        .Where(pm => pm.Rows > 0
                            && !string.Equals(pm.Part, anchor, StringComparison.Ordinal))
                        .SelectMany(pm =>
                        {
                            var hashes = new List<string>
                            {
                                p.CapHashes.TryGetValue(pm.Part, out var h) ? h : $"REPLACE_{pm.Part}_ib",
                            };
                            hashes.AddRange(p.TierMeta
                                .Where(t => string.Equals(t.Part, pm.Part, StringComparison.Ordinal))
                                .Select(t => t.Hash));
                            hashes.AddRange(p.PresenceHashes?.GetValueOrDefault(pm.Part)
                                ?? (IReadOnlyList<string>)Array.Empty<string>());
                            return hashes.Select(x => (Name: pm.Part, Hash: x));
                        }));
            })
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(g => new WitnessLatch(MeshLatch(g.Key),
                g.Select(x => x.Hash).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();
        // the src_ prefix is the routing key mesh latches carry through RouteSightings — a caller latch
        // wearing it would collide with the namespace and mis-route, so it refuses like every other
        // name collision in this file
        foreach (var l in req.Latches ?? Array.Empty<WitnessLatch>())
            if (l.Name.StartsWith("src_", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"latch '{l.Name}' collides with the mesh-latch namespace (src_*)");
        var allLatches = (req.Latches ?? Array.Empty<WitnessLatch>()).Concat(meshLatches).ToList();
        var sightings = RouteSightings(allLatches, units, hides, req.ScopedRetextures, rigidOwner.Keys,
            new HashSet<string>(guards.Keys, StringComparer.Ordinal),
            LiveSightings(req.TwinSightings, guards.Values), req.StockRamps, req.StockDraws);
        // Grouped ahead of the retexture text: a group at an unreplaced part's own draw is written inside
        // that part's section, which the retexture text composes.
        var materialPatches = MaterialPatchGroups(req.MaterialPatches, pipes, rigids, req.OutDir,
            req.StockDraws);
        WriteMaterialPatchShaders(req.OutDir, materialPatches);
        // A hidden mesh that a scoped retexture also anchors on owns ONE section; this is where the
        // retexture hands its body over, and the hide section below writes it out.
        var hideScope = HideScope(hides);
        string retexIni = req.Retextures is { Count: > 0 } || req.ScopedRetextures is { Count: > 0 }
                          || req.StockRamps is { Count: > 0 } || req.StockDraws is { Count: > 0 }
                          || guards.Count > 0
            ? RetexIni(req.Retextures ?? Array.Empty<RetexEntry>(), req.OutDir, req.ToggleKey,
                req.ScopedRetextures, units, sightings, rigidOwner, guards, tagKinds,
                req.StockRamps, hideScope, req.StockDraws, materialPatches)
            : "";
        string ini = EmitIni(pipes, rigids, units, hides, sightings, slotTags, propertyTags, slimParts,
            req.ToggleKey, req.HideClaims, req.Retextures ?? Array.Empty<RetexEntry>(),
            req.ScopedRetextures ?? Array.Empty<ScopedRetexEntry>(), allLatches,
            req.KeysStartingOff, guards, req.StockRamps, materialPatches, req.KeyCycles,
            req.HiddenFlags, hideScope, req.ShownFlags, req.PersistToggleKey, req.StockDraws, req.AppVersion, req.MeshLabels,
            tail: retexIni);
        // A slim operator's vertex list is written with the operator and declared with the mesh; a mesh
        // that only cached pipelines recover reads it through its packet instead, so a list no section
        // binds is neither declared nor shipped. Its offsets stay: the kernel binds them.
        var boundSel = new HashSet<string>(
            Regex.Matches(ini, @"^[^\[\n][^\n]* = Resource_(.+)_Sel$", RegexOptions.Multiline).Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);
        ini = Regex.Replace(ini,
            @"^\[Resource_(?<m>[^\]\n]+)_Sel\]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = \k<m>_sel\.buf\n",
            m => boundSel.Contains(m.Groups["m"].Value) ? m.Value : "", RegexOptions.Multiline);
        foreach (string name in slimParts)
            if (!boundSel.Contains(name))
                File.Delete(Path.Combine(req.OutDir, $"{name}_sel.buf"));
        File.WriteAllText(Path.Combine(req.OutDir, "mod.ini"), DrawSelectorIni.Lower(ini));
        // A recover shader is written for every solved operator; a mesh whose every pipeline caches
        // reads its operator from the cache kernel instead, and the shader is read by nothing.
        var recoverShaders = new HashSet<string>(
            Regex.Matches(ini, @"^cs = (recover_.+_cs\.hlsl)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(req.OutDir, "recover_*_cs.hlsl"))
            if (!recoverShaders.Contains(Path.GetFileName(file))) File.Delete(file);

        foreach (var grouped in mergedTierWarnings.GroupBy(w => (w.AffectedPart, w.Tier)))
        {
            var owners = grouped.SelectMany(w => w.OwningParts)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var bones = grouped.SelectMany(w => w.Bones).OrderBy(b => b.Hash)
                .GroupBy(b => b.Hash).Select(g => g.First()).ToArray();
            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var supplied in grouped.Select(w => w.DisplayNames).OfType<IReadOnlyDictionary<string, string>>())
                foreach (var pair in supplied) displayNames.TryAdd(pair.Key, pair.Value);
            var issue = new MergedTierWarning(grouped.Key.AffectedPart, grouped.Key.Tier, owners, bones,
                displayNames.Count > 0 ? displayNames : null);
            warnings.Add(FormatMergedTierWarning(issue));
            diagnostics.Add(FormatMergedTierDiagnostic(issue));
        }

        return new Result(req.OutDir, ubTotal, vcountTotal, warnings, diagnostics,
            palette.Count > 0 ? palette : null);
    }

    /// <summary>A mod with no Replace verbs — retextures and/or hides only. No compute pipeline, geometry,
    /// neutral maps or pass flags: a hide keys on the mesh's index buffer and a retexture on the stock
    /// texture's own hash, so neither needs to know which pass is drawing. Throws when BOTH lists are
    /// empty.</summary>
    public Result BuildOverlaysOnly(string outDir, IReadOnlyList<RetexEntry>? entries,
        IReadOnlyList<string>? hideHashes = null, string? modKey = null,
        IReadOnlyDictionary<string, IReadOnlyList<HideClaim>>? hideClaims = null,
        IReadOnlyList<ScopedRetexEntry>? scopedEntries = null,
        IReadOnlyList<WitnessLatch>? latches = null,
        IReadOnlyCollection<string>? keysStartingOff = null,
        IReadOnlyList<TwinGuard>? twinGuards = null,
        IReadOnlyList<TwinSighting>? twinSightings = null,
        IReadOnlyList<StockRampBind>? stockRamps = null,
        IReadOnlyList<KeyCycle>? keyCycles = null,
        IReadOnlyList<ShownFlag>? shownFlags = null,
        bool persistToggleKey = false,
        IReadOnlyDictionary<string, string>? meshLabels = null,
        IReadOnlyList<MaterialPatchEmission>? materialPatches = null,
        IReadOnlyList<StockDrawSite>? stockDraws = null, string? appVersion = null)
    {
        var retex = entries ?? Array.Empty<RetexEntry>();
        var shown = shownFlags ?? Array.Empty<ShownFlag>();
        var scoped = scopedEntries ?? Array.Empty<ScopedRetexEntry>();
        var ramps = stockRamps ?? Array.Empty<StockRampBind>();
        var sites = stockDraws ?? Array.Empty<StockDrawSite>();
        var guards = GuardsByHash(twinGuards);
        // deduped for the same reason the pooled path dedupes: one hash gets one owning TextureOverride —
        // the runtime runs every match-passing section, so a second hide would just repeat the same skip
        var hides = (hideHashes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        if (retex.Count == 0 && scoped.Count == 0 && hides.Count == 0 && ramps.Count == 0 && sites.Count == 0)
            throw new InvalidOperationException(
                "overlay-only build with no retextures, no ramp picks, no shading changes and no hides");
        // No pipelines here, so nothing slot-tags an anchor's stock maps: every hash is a retexture's or a
        // twin guard's.
        RefuseTagCollisions(Array.Empty<(string Hash, string Part)>(),
            scoped.Select(e => (Hash: e.StockHash, Part: e.Part)),
            MintedProbeTagHashes(guards.Values, ramps, sites).Select(h => (Hash: h, Part: "")),
            BufferTags(meshLabels, hides
                .Concat((latches ?? Array.Empty<WitnessLatch>()).SelectMany(l => l.WitnessIbs))
                .Concat(scoped.SelectMany(e => e.Images.SelectMany(i => i.Anchors.Select(a => a.Hash))))
                .Concat(ramps.Select(b => b.IbHash))
                .Concat(sites.Select(s => s.IbHash))
                .Concat((twinSightings ?? Array.Empty<TwinSighting>()).Select(t => t.Hash))));
        Directory.CreateDirectory(outDir);
        // no pipelines here, so no hash is capture-claimed; a sighting still routes into the hide or
        // scoped-anchor section that owns its ib
        var units = BuildCaptureUnits(Array.Empty<PipelineEmission>(), modKey, guards);
        var sightings = RouteSightings(latches, units, hides, scoped,
            twins: LiveSightings(twinSightings, guards.Values), stockRamps: ramps, stockDraws: sites);
        var patchGroups = MaterialPatchGroups(materialPatches, new List<PipelineEmission>(),
            new List<RigidEmission>(), outDir, sites);
        WriteMaterialPatchShaders(outDir, patchGroups);
        var P = new StringBuilder();
        // an overlay-only mod has no [Constants] of its own, so a keyed or latched one declares its
        // variables here or every gate would test an undefined name
        var overlayKeys = hides.SelectMany(h => HideClaimKeys(hideClaims, h).Select(k => (KeyRef?)k))
            .Concat(retex.SelectMany(r => r.Images).Select(i => i.ToggleKey))
            .Concat(scoped.SelectMany(r => r.Images).Select(i => i.ToggleKey))
            .Concat(ramps.Select(b => b.ToggleKey))
            .Concat(sites.Select(s => s.ToggleKey))
            // a change gated on a content flag carries no key term of its own, so the key that raises the
            // flag is declared from the flag's own positions or from nowhere
            .Concat(shown.SelectMany(f => f.WhenAny).Select(k => (KeyRef?)k)).ToList();
        var overlayKeyNames = overlayKeys.Select(k => k?.Key).ToList();
        var declared = ModKeys.Distinct(new[] { modKey }.Concat(overlayKeyNames));
        var lat = latches ?? Array.Empty<WitnessLatch>();
        if (declared.Count > 0 || lat.Count > 0 || scoped.Count > 0 || guards.Count > 0
            || ramps.Count > 0 || shown.Count > 0)
        {
            P.Append("[Constants]\n");
            if (scoped.Count > 0) P.Append($"global ${VarRetexProbe} = 0\nglobal ${VarRetexSlot} = 0\n");
            if (ramps.Count > 0)
                P.Append($"global ${VarRampSlot} = 0\nglobal ${VarStockRampProbe} = 0\n");
            if (guards.Count > 0)
            {
                // the slot probe belongs to the guards that carry tags; a build whose verdicts all arrive
                // from sightings never reads a slot
                if (guards.Values.Any(g => g.Tags.Count > 0)) P.Append($"global ${VarProbe} = 0\n");
                foreach (var v in TwinVars(guards.Values)) P.Append($"global ${v} = 0\n");
                // the multi-verdict guards' scratch, rewritten at every guard it opens rather than carried
                if (TwinScratchNeeded(guards.Values, hides)) P.Append($"global ${VarTwinOk} = 0\n");
            }
            foreach (var l in lat)
                P.Append($"global ${GateVar(l.Name)} = 0\nglobal ${SeenVar(l.Name)} = 0\n");
            // one per change answering more than one position of its key group, recomputed right below
            // so a session opens with the answer its launch positions imply
            foreach (var flag in shown) P.Append($"global ${ShownVar(flag.Name)} = 0\n");
            P.Append(KeyDeclarations(declared, modKey, keysStartingOff, keyCycles, persistToggleKey));
            if (shown.Count > 0)
            {
                P.Append($"run = {SectionRecomputeHidden}\n");
                // a persist key's saved position arrives from the runtime's user config, which is loaded
                // last — after this run — so the flags it implies are recomputed once more post-restore
                if (declared.Any(k =>
                        CycleFor(k, modKey, keysStartingOff, keyCycles, persistToggleKey).Persist))
                    P.Append($"post run = {SectionRecomputeHidden}\n");
            }
            P.Append("\n");
            P.Append(RecomputeHiddenIni(Array.Empty<HiddenFlag>(), shown));
        }
        if (lat.Count > 0)
        {
            P.Append("[Present]\n");
            foreach (var l in lat)
                P.Append($"${GateVar(l.Name)} = ${SeenVar(l.Name)}\n${SeenVar(l.Name)} = 0\n");
            P.Append("\n");
        }
        P.Append(KeysIni(modKey, overlayKeyNames, keysStartingOff, keyCycles, shown.Count > 0));
        P.Append(WitnessIni(sightings));
        EmitMaterialPatchSections(P, patchGroups);
        // Built ahead of the hide sections and appended after them, unchanged in the emitted order: a
        // scoped retexture anchored on a hidden draw hands its body to that draw's one section, and this
        // is what fills it.
        var hideScope = HideScope(hides);
        string retexTail = retex.Count > 0 || scoped.Count > 0 || guards.Count > 0 || ramps.Count > 0
                           || sites.Count > 0
            ? RetexIni(retex, outDir, modKey, scoped, units, sightings, null, guards,
                null, ramps, hideScope, sites, patchGroups)
            : "";
        for (int i = 0; i < hides.Count; i++)
        {
            OpenTextureOverride(P, $"Hide_{i}", hides[i]);
            // sighting UNGATED: a witness silenced while a key was off would read the outfit as
            // absent the frame it comes back on
            if (sightings.ByHash.TryGetValue(hides[i], out var seen))
                foreach (var line in seen) P.Append(line).Append('\n');
            // where this hash also fires on a sibling mesh's draws, each skip waits for the probe to find
            // its own hidden mesh's tagged texture bound
            AppendHideSkips(P, guards, hideClaims, hides[i], modKey, keyCycles);
            foreach (var line in HideScopeLines(hideScope, hides[i])) P.Append(line).Append('\n');
            P.Append("\n");
        }
        P.Append(retexTail);
        // the header says what the finished file holds, so it is written last and goes first
        var (_, overlayPasses, overlayRetex) = IniMarkers(P.ToString());
        P.Insert(0, IniHeader(appVersion, new ModSummary(0,
            hides.Select(h => meshLabels is not null && meshLabels.TryGetValue(h, out var l) ? l : h)
                .Distinct(StringComparer.Ordinal).Count(),
            retex.Select(r => r.Hash).Concat(scoped.Select(e => e.StockHash))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ramps.Count > 0 || patchGroups.Count > 0, modKey,
            StateKeys(modKey, overlayKeyNames, keyCycles))
        {
            MaterialPasses = overlayPasses,
            Retextures = overlayRetex,
        }) + "\n");
        File.WriteAllText(Path.Combine(outDir, "mod.ini"), DrawSelectorIni.Lower(P.ToString()));
        return new Result(outDir, 0, 0, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>One empty line list per hidden hash, for the scoped-retexture bodies that anchor on one of
    /// them. Keyed rather than looked up on a list because a body is handed over by hash.</summary>
    static Dictionary<string, List<string>> HideScope(IEnumerable<string> hides) =>
        hides.Distinct(StringComparer.Ordinal)
            .ToDictionary(hash => hash, _ => new List<string>(), StringComparer.Ordinal);

    /// <summary>The folded-in body one hide section carries, or nothing.</summary>
    static IReadOnlyList<string> HideScopeLines(IReadOnlyDictionary<string, List<string>>? scope,
        string hash) => scope is not null && scope.TryGetValue(hash, out var lines)
            ? lines : Array.Empty<string>();

    /// <summary>Every key position any claim on one hash names, for the declarations: a variable one
    /// claim tests has to exist whichever claim the build met first.</summary>
    static IEnumerable<KeyRef> HideClaimKeys(IReadOnlyDictionary<string, IReadOnlyList<HideClaim>>? claims,
        string hash) =>
        claims is not null && claims.TryGetValue(hash, out var named)
            ? named.SelectMany(claim => claim.Keys) : Array.Empty<KeyRef>();

    /// <summary>The claims one hide answers. A hash no claim names hides in every state: at each of its
    /// twin guard's own verdicts where a guard holds it, else at every draw.</summary>
    static IReadOnlyList<HideClaim> HideClaimsOn(IReadOnlyDictionary<string, IReadOnlyList<HideClaim>>? claims,
        string hash, TwinGuard? guard)
    {
        if (claims is not null && claims.TryGetValue(hash, out var named) && named.Count > 0) return named;
        return guard is null
            ? new[] { new HideClaim(Array.Empty<KeyRef>()) }
            : guard.OwnVerdicts.Select(v => new HideClaim(Array.Empty<KeyRef>(), Verdict: v)).ToList();
    }

    /// <summary>A hide section's skips: the OR across every claim on the hash. Each claim's key positions
    /// stand on that claim's OWN presence latch and, under a twin guard, its OWN mesh's verdict, so one
    /// change's states never skip another change's outfit or sibling mesh. Where every claim names the
    /// same verdict the guard opens once around them all, which is the section a single claim has always
    /// emitted. A skip whose conditions another skip already covers is dropped.</summary>
    void AppendHideSkips(StringBuilder P, IReadOnlyDictionary<string, TwinGuard> guards,
        IReadOnlyDictionary<string, IReadOnlyList<HideClaim>>? claims, string hash, string? modKey,
        IReadOnlyList<KeyCycle>? keyCycles)
    {
        var guard = guards.TryGetValue(hash, out var found) ? found : null;
        var onHash = HideClaimsOn(claims, hash, guard);
        foreach (var claim in onHash)
            if (claim.Verdict is { } v && (guard is null || !guard.OwnVerdicts.Contains(v)))
                throw new InvalidOperationException(
                    $"the hide on {hash} claims twin verdict {v}, which no guard on that hash admits");
        var verdicts = onHash.Select(claim => claim.Verdict).Distinct().ToList();
        int? shared = guard is not null && verdicts.Count == 1 ? verdicts[0] : null;
        if (guard is not null) AppendTwinProbe(P, guard);
        if (shared is { } common) P.Append($"if ${guard!.Var} == {common}\n");
        var gates = new List<Gate>();
        foreach (var claim in onHash)
        {
            var raw = new List<GateVarState>(LatchTerms(claim.Latch) ?? Array.Empty<GateVarState>());
            if (shared is null && claim.Verdict is { } own) raw.Add(new GateVarState(guard!.Var, own));
            var mine = claim.Keys.Count == 0
                ? new List<Gate> { new(new KeyRef?[] { ModTerm(modKey) }, raw) }
                : claim.Keys.Select(term => new Gate(new KeyRef?[] { ModTerm(modKey), term }, raw)).ToList();
            gates.AddRange(CollapseSkips(mine, modKey, keyCycles));
        }
        foreach (var gate in CollapseSkips(Uncovered(gates), modKey, keyCycles))
        {
            gate.Open(P);
            P.Append("handling = skip\n");
            gate.Close(P);
        }
        if (shared is not null) P.Append("endif\n");
    }

    /// <summary>The gates no other gate already covers. A duplicate skips exactly where its first copy
    /// does, and a gate standing on every term of another plus more skips only where the other already
    /// does.</summary>
    static List<Gate> Uncovered(List<Gate> gates)
    {
        var distinct = gates.GroupBy(gate => gate.Id, StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        return distinct.Where(gate => !distinct.Any(other => other.Terms.Length < gate.Terms.Length
            && other.Terms.All(gate.Terms.Contains))).ToList();
    }

    /// <summary>The gate a pipeline's draw, and every pass run for it, sits inside: the mod's key, the
    /// change's own key, its presence latch and content flag, and the hider flag of a group that takes it off
    /// screen.</summary>
    static Gate DrawGateOf(PipelineEmission pipe, string? modKey)
    {
        var contentVars = With(LatchTerms(pipe.Latch), ShownTerm(pipe.ShownBy));
        return new Gate(new KeyRef?[] { ModTerm(modKey), pipe.ToggleKey },
            pipe.HiddenBy is null ? contentVars : With(contentVars, HiddenTerm(pipe.HiddenBy)));
    }

    /// <summary><paramref name="body"/> run while any one of <paramref name="gates"/> holds, once whichever
    /// of them do: bare where one always holds, inside that gate where only one is left once the gates
    /// another covers are dropped, and otherwise under one test joining each gate's conditions with
    /// <c>&amp;&amp;</c> and the gates with <c>||</c>.</summary>
    static IEnumerable<string> WrapAny(IReadOnlyList<Gate> gates, IEnumerable<string> body)
    {
        var any = Uncovered(gates.ToList());
        if (any.Count == 0 || any.Any(g => g.IsAlwaysOn)) return body;
        if (any.Count == 1) return any[0].Wrap(body);
        return new[]
            {
                "if " + string.Join(" || ", any.Select(g =>
                    "(" + string.Join(" && ", g.Terms.Select(t => $"${t.Var} == {t.State}")) + ")")),
            }
            .Concat(body).Append("endif");
    }

    // ---- ini emission (LF; the emission contract) ---------------------------------------------------

    /// <summary>One emitted capture section: a unique ib hash, its capture lines (deduped across the
    /// pipelines that pool this mesh), whether ANY pipeline suppresses the draw, and the chain runs of
    /// every pipeline anchored at this mesh.</summary>
    sealed class CaptureUnit
    {
        public required string SectionName;
        public required string Hash;
        /// <summary>Gates of the pipelines that suppress this mesh, first-seen order, deduped. An ALWAYS-ON
        /// gate wins outright; otherwise each gate emits its own guarded <c>handling = skip</c>, so the
        /// skip is the OR across the pipelines whose keys are on. A part pooled by a reverting pipeline and
        /// a hiding one carries both gates; the hiding gate names no key, so the OR holds it suppressed.</summary>
        public readonly List<Gate> SkipGates = new();
        public readonly List<string> CaptureLines = new();
        public readonly List<string> RunLines = new();
        /// <summary>Draw-scoped retexture blocks whose anchor IS this mesh. They run after the chain, so
        /// the pipeline's own slot probe reads the stock textures' tags rather than a rebound
        /// replacement's, and the block's own save/probe/bind/restore then repaints the vanilla draw.</summary>
        public readonly List<string> ScopeLines = new();
        /// <summary>Presence-latch assignments this section records ahead of everything else. A section
        /// under a twin guard keeps them here: either sibling's draw proves the outfit is on screen, so
        /// the sighting must not wait on the guard's verdict.</summary>
        public readonly List<string> SightingLines = new();
        readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        readonly HashSet<string> _skipSeen = new(StringComparer.Ordinal);
        public void Capture(string line) { if (_seen.Add(line)) CaptureLines.Add(line); }
        readonly HashSet<string> _sightSeen = new(StringComparer.Ordinal);
        public void Sight(string line) { if (_sightSeen.Add(line)) SightingLines.Add(line); }
        // no dedupe: chain blocks legitimately repeat structural lines (if/endif) across pipelines
        public void Run(string line) => RunLines.Add(line);
        public void Suppress(Gate gate) { if (_skipSeen.Add(gate.Id)) SkipGates.Add(gate); }
        public bool Skips => SkipGates.Count > 0;
        /// <summary>The pooled route's ring of each source mesh this hash draws, first-seen order, with the
        /// draw gate of every pipeline reading it: the ring passes run while any of those pipelines is on
        /// (see <see cref="WrapAny"/>), once whichever of them are.</summary>
        public readonly List<(string Mesh, List<Gate> Gates)> Rings = new();
        public void Ring(string mesh, Gate gate)
        {
            int at = Rings.FindIndex(r => r.Mesh == mesh);
            if (at < 0) Rings.Add((mesh, new List<Gate> { gate }));
            else if (Rings[at].Gates.All(g => g.Id != gate.Id)) Rings[at].Gates.Add(gate);
        }
        /// <summary>Per-submesh draw routing for this section's hash, when the replaced mesh has several
        /// submeshes: the donor draw leaves the chain above and moves into extra sections on the same
        /// hash, each matching one vanilla submesh draw's shape, so donor range k renders under submesh
        /// k's own bound material instead of every range drawing at every material's draw. The extra
        /// sections' names extend this section's, and equal match_priority runs same-hash sections in
        /// name order, so the capture/compute chain always runs before the routed draw. A LIST because
        /// several pipelines can anchor on one hash — the merged section carried every pipeline's draw
        /// line, and the routed sections owe every pipeline its draws the same way.</summary>
        public readonly List<RoutedDraw> RoutedDraws = new();
    }

    /// <summary>One pipeline's routed donor draw: the command-list namespace (the pipeline suffix), the
    /// replaced mesh's vanilla shape set at THIS section's detail level, the draw's gate, and the donor's
    /// own draw count. Donor range k belongs to vanilla submesh k; ranges past the last vanilla submesh
    /// join it.
    ///
    /// <para><paramref name="Map"/> and <paramref name="AnchorShapes"/> are the tier reading: the donor
    /// range's material position is read off the ANCHOR's (lod0) shapes, then the map says which of this
    /// tier's positions carries that region — or that the tier draws it nowhere, in which case the range
    /// is not drawn here at all. Both null is the lod0 reading, where the position is the tier's own.</para>
    ///
    /// <para><paramref name="Pose"/> holds the pose route's passes for this draw — the gather, the palette
    /// passes (on the pooled route also the anchor capture and each source mesh's pick and take) and the
    /// skin passes — which every routed section runs inside this draw's gate before its lists,
    /// skinning only the pieces of the ranges that section draws, so the passes run only where a replacement
    /// draws. The gather reads that draw's own vertices, except for a lower-detail mesh that supplies no
    /// bones of its own, whose gather reads the lod0 capture's reference as the build log says. Null for a
    /// pooled chain, whose compute stays in the capture section, and for a rigid draw.</para></summary>
    sealed record RoutedDraw(string Sfx, DrawShapeSet Shapes, Gate DrawGate, int DonorDraws,
        bool IsRigid = false, DrawShapeSet? AnchorShapes = null, TierMaterialMap? Map = null,
        PosePasses? Pose = null)
    {
        /// <summary>The material position of this section's mesh that donor range <paramref name="donorDraw"/>
        /// renders under, or -1 when the tier carries that range's region nowhere and it is not drawn.</summary>
        public int TargetPosition(int donorDraw) =>
            RoutedTargetPosition(Shapes, AnchorShapes, Map, donorDraw);

        /// <summary>Every donor range this section draws, in range order. Short of the full list when the
        /// map drops one, which is what keeps a dropped range out of the whole-mesh pass too.</summary>
        public IEnumerable<int> CarriedDraws =>
            Enumerable.Range(0, DonorDraws).Where(di => TargetPosition(di) >= 0);

        /// <summary>The map leaves at least one donor range with nowhere to draw at this detail level.</summary>
        public bool DropsAnyDraw => RoutedDropsAnyDraw(Shapes, AnchorShapes, Map, DonorDraws);
    }

    /// <summary>Which material position of the section's own mesh donor range <paramref name="donorDraw"/>
    /// renders under. WITHOUT a map that is the fold on the section's own shapes, which is what the lod0
    /// draw and every legacy caller mean. WITH one the range's position is read on the anchor's (lod0's)
    /// shapes and then carried to this tier, and -1 says the tier draws that region nowhere.</summary>
    static int RoutedTargetPosition(DrawShapeSet shapes, DrawShapeSet? anchorShapes, TierMaterialMap? map,
        int donorDraw)
    {
        if (map is null) return DrawMaterialFold.TargetMaterialPosition(shapes, donorDraw);
        int lod0 = DrawMaterialFold.TargetMaterialPosition(anchorShapes ?? shapes, donorDraw);
        if (lod0 < 0) return -1;
        // a position the map says nothing about keeps the fold it has always taken, never a dropped range
        return map.TryCarrier(lod0, out var carrier)
            ? carrier ?? -1
            : DrawMaterialFold.TargetMaterialPosition(shapes, donorDraw);
    }

    /// <summary>At least one donor range has nowhere to draw under this map. The reason a one-submesh tier
    /// still routes: holding a range back needs the per-range lists.</summary>
    static bool RoutedDropsAnyDraw(DrawShapeSet shapes, DrawShapeSet? anchorShapes, TierMaterialMap? map,
        int donorDraws) =>
        map is not null && Enumerable.Range(0, donorDraws)
            .Any(di => RoutedTargetPosition(shapes, anchorShapes, map, di) < 0);

    /// <summary>The build's capture sections, in emission order and by the hash each one owns.</summary>
    /// <summary>The capture sections, and every pose block they and their routed draw sections run
    /// (<see cref="PosePasses.Block"/>), declared with the pose sections.</summary>
    sealed record CaptureUnits(List<CaptureUnit> Ordered, Dictionary<string, CaptureUnit> ByHash, List<string> PoseBlocks);

    /// <summary>
    /// One capture section per unique ib hash, merged across the pipelines that pool the mesh: a part in
    /// two pools is captured once and its section's skip is the OR across them. Builds structure only —
    /// nothing here writes text, so ownership is known before any section is emitted.
    /// <para>Identity is the HASH; the part name only proposes a section name. One part name can carry two
    /// DIFFERENT hashes across pipelines — one physical mesh keyed on its vb1 in one outfit's signature
    /// index and on its ib in another — and two sections under one name leave the second dropped at parse
    /// time, so a name already issued sends the second unit to a disambiguated one.</para>
    /// </summary>
    static CaptureUnits BuildCaptureUnits(IReadOnlyList<PipelineEmission> pipes, string? modKey,
        IReadOnlyDictionary<string, TwinGuard> guards,
        IReadOnlyList<HiddenFlag>? hiddenFlags = null)
    {
        var poseBlocks = new List<string>();
        // A hash routes its donor draw per submesh when the replaced mesh really has several DRAWABLE
        // submeshes (a zero-index-count submesh is a material slot with no geometry — the game issues no
        // draw for it), OR when this tier's material map leaves a donor range with nowhere to draw, since
        // holding that range back needs the per-range lists even on a one-submesh tier. Never when the hash
        // carries a twin guard: a guarded section's draw must stay inside the guard's verdict, so a guarded
        // target keeps the draw in its capture section (every range at every fire).
        DrawShapeSet? RoutedShapes(DrawShapeSet? shapes, string hash, RoutedDraw? routed = null)
            => shapes is not null && !guards.ContainsKey(hash)
                && (shapes.Shapes.Count(sh => sh.Count > 0) > 1 || (routed?.DropsAnyDraw ?? false))
                ? shapes : null;
        var ordered = new List<CaptureUnit>();
        var byHash = new Dictionary<string, CaptureUnit>(StringComparer.Ordinal);
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        // The proposed name when it is free, else the hash appended to it, else a counter on top of that.
        // Every candidate is checked against the names already issued, so no two units can share one however
        // many collide. The hash is a caller-supplied string, so its non-alphanumerics collapse to '_' the
        // way part names' do (a pool part without a capture hash carries a REPLACE_*_ib placeholder).
        string IssueName(string proposed, string hash)
        {
            if (takenNames.Add(proposed)) return proposed;
            var sb = new StringBuilder(proposed).Append('_');
            foreach (char c in hash) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string stem = sb.ToString(), pick = stem;
            for (int n = 2; !takenNames.Add(pick); n++) pick = $"{stem}_{n}";
            return pick;
        }
        CaptureUnit Unit(string hash, string sectionName)
        {
            if (!byHash.TryGetValue(hash, out var u))
                ordered.Add(byHash[hash] = u = new CaptureUnit
                {
                    SectionName = IssueName(sectionName, hash),
                    Hash = hash,
                });
            return u;
        }

        foreach (var pipe in pipes)
        {
            string sfx = pipe.Sfx;
            string anchor = pipe.PartMeta[pipe.AnchorIdx].Part;
            // The pipeline's gates: mod key, own key, presence latch. Captures stay UNGATED — a keyed-off
            // pipeline that stopped capturing would have no recovery input the frame it comes back on, and
            // would pose its owned bones with garbage. Suppression and draw gate SEPARATELY: sharing one
            // gate returns the vanilla part when off; dropping the tier-2 key from the suppression gate
            // leaves the part absent. The compute chain sits inside the draw gate, so off dispatches nothing.
            // The hider flag rides the DRAW gate only. What the vanilla draw does while another group
            // hides this part is that group's account, emitted below as a guarded skip per hiding state;
            // putting the flag on the suppression too would say the same thing twice and read as though
            // hiding gave the game's part back.
            var latchVars = LatchTerms(pipe.Latch);
            // A change answering more than one position carries no key term: what stands in for it is the
            // content flag, which is 1 in every one of those positions.
            var contentVars = With(latchVars, ShownTerm(pipe.ShownBy));
            var contentGate = new Gate(new KeyRef?[] { ModTerm(modKey), pipe.ToggleKey }, contentVars);
            var drawGate = DrawGateOf(pipe, modKey);
            var skipGate = pipe.HideWhenOff
                ? new Gate(new KeyRef?[] { ModTerm(modKey) }, latchVars) : contentGate;
            // per POOL PART: hiding the replaced part leaves this pipeline's pool mates drawing their
            // own vanilla, so their skips never borrow these gates
            var hiddenSkips = (pipe.SuppressWhen
                    ?? new Dictionary<string, IReadOnlyList<KeyRef>>(StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => SuppressGates(pair.Value, modKey, latchVars),
                    StringComparer.Ordinal);
            for (int idx = 0; idx < pipe.PartMeta.Count; idx++)
            {
                var part = pipe.PartMeta[idx].Part;
                string h = pipe.CapHashes.TryGetValue(part, out var cv) ? cv : $"REPLACE_{part}_ib";
                var u = Unit(h, $"Cap_{part}");
                if (pipe.PartMeta[idx].Rows > 0)
                {
                    // On the pooled route a part the replacement takes rows from writes its ring slot at
                    // every draw the pipeline is on for instead of a reference, and the anchor keeps its
                    // reference only for a lower-detail mesh of its own that gathers out of it.
                    if (pipe.PoseRoute is { Sources: not null } pooledRoute)
                    {
                        if (idx != pipe.AnchorIdx) u.Ring(part, drawGate);
                        else if (pooledRoute.TierKernel.Values.Contains(part)) u.Capture($"Resource_{part}_Posed = ref vb0");
                    }
                    else u.Capture($"Resource_{part}_Posed = ref vb0");
                    // The constants are read by the constants convert, and by an AT-DRAW member's rebase
                    // of the anchor's. A witness-converted pool without such a member reads none, and
                    // the copy is a resource copy at every fire of the part. The pose route reads none.
                    if (pipe.PoseRoute is null
                        && (!pipe.Lod0WitnessConvert || (idx == pipe.AnchorIdx && pipe.GroupMembers.Any(m => m.AtDraw))))
                        u.Capture($"Resource_{part}_CB = copy vs-cb1");
                }
                // The sticky flag an AT-DRAW member lod0 waits on, set right where the anchor's constants
                // land: this is the ONE capture that fills the CB its rebase reads. Never reset, so it
                // stays 1 for the rest of the session once the anchor has drawn once. In-chain members
                // need no flag — the chain itself runs at the anchor's draw.
                if (idx == pipe.AnchorIdx && pipe.GroupMembers.Any(m => m.AtDraw))
                    u.Capture($"${GroupCbVar(sfx)} = 1");
                if (idx == pipe.AnchorIdx && pipe.Vb1Variants.Count > 0)
                    u.Capture($"${Vb1Var(sfx)} = 0");
                if (pipe.NoSkip?.Contains(part) != true) u.Suppress(skipGate);
                // one guarded skip per state that hides THIS part: hidden means nothing on screen, so the
                // vanilla draw the content gate closes over in those positions goes too. Owed by a NoSkip
                // pool mate as well — leaving its vanilla draw running is what this pipeline says about
                // its OWN gate, and says nothing about a position that takes the part off screen.
                if (hiddenSkips.TryGetValue(part, out var partSkips))
                    foreach (var g in partSkips) u.Suppress(g);
                if (idx == pipe.AnchorIdx)
                {
                    var chain = new List<string>();
                    PosePasses? pose = null;
                    if (pipe.PoseRoute is { } route0)
                    {
                        // the pose passes run right before each draw of the replacement: the gather reads
                        // that draw's own vertices, the palette pass recovers this copy's pose from them,
                        // and a skin pass per piece writes the stream the draw list binds next. Where the
                        // draw routes they ride the routed sections, which fire only where a replacement
                        // draws; the capture section fires at every draw of the mesh, those no routed
                        // section names included, where a pass would write a stream nothing reads.
                        pose = PosePassesFor(route0, $"CustomShaderGather_{part}", part, sfx, poseBlocks);
                    }
                    else
                    {
                        // recover/convert/skin once per frame (the flag resets in [Present]); the DRAW runs
                        // at every fire — suppressing draws kills shadows/outlines
                        chain.Add($"if $zz_done_{sfx} == 0");
                        for (int pi = 0; pi < pipe.PartMeta.Count; pi++)
                            RecoverRun(chain, pipe, pi, pipe.PartMeta[pi].Part, sfx);
                        if (pipe.Lod0WitnessConvert)
                            chain.Add($"run = CustomShaderConvertW_{sfx}");
                        else
                            for (int chunk = 0; chunk < ComputeTemplates.ConvertChunks(pipe.PartMeta.Count); chunk++)
                                chain.Add($"run = {ConvertSection(sfx, chunk)}");
                        MemberRuns(chain, pipe, sfx);
                        TieRuns(chain, pipe, sfx);
                        chain.Add($"run = CustomShaderSkin_{sfx}");
                        chain.Add($"$zz_done_{sfx} = 1");
                        chain.Add("endif");
                    }
                    if (RoutedShapes(pipe.AnchorShapes, h) is { } routed0)
                        u.RoutedDraws.Add(new RoutedDraw(sfx, routed0, drawGate, pipe.Draws.Count, Pose: pose));
                    else
                    {
                        if (pose is not null) chain.Add(pose.Block(part));
                        chain.Add($"run = CommandListDraw_{sfx}");
                    }
                    // a routed pose draw leaves nothing to run here, and an empty gate says nothing
                    if (chain.Count > 0)
                        foreach (var line in drawGate.Wrap(chain)) u.Run(line);
                }
            }

            // tier captures: skip + per-tier recovery; the ANCHOR part's tiers run the whole chain so the
            // donor draws in every context that picks this tier. Parts without a same-suffix tier fall
            // back to their lod0 recover (its captured ref reads current frame-start-uploaded data). Tier
            // chains use the constants-free WITNESS convert (see the witness block in Build).
            foreach (var t in pipe.TierMeta)
            {
                var u = Unit(t.Hash, $"Cap_{t.Name}");
                // the pooled route reads a recovering tier through its ring where another part's, and never
                // reads the anchor's own tier's reference: that tier's gather reads its draw directly
                if (t.Rows > 0)
                {
                    if (pipe.PoseRoute?.Sources is null) u.Capture($"Resource_{t.Name}_Posed = ref vb0");
                    else if (t.Part != anchor) u.Ring(t.Name, drawGate);
                }
                if (pipe.NoSkip?.Contains(t.Part) != true) u.Suppress(skipGate);
                // the same per-part account the lod0 walk above emits, on the part's OTHER draws: hidden
                // means nothing on screen at any detail, and LOD choice is not distance-only, so a tier
                // left running would put the part back the moment the renderer picked that tier. Keyed on
                // the part, which is what SuppressWhen and the lod0 walk are both keyed on, and owed by a
                // NoSkip pool mate here for the same reason it is owed there.
                if (hiddenSkips.TryGetValue(t.Part, out var tierSkips))
                    foreach (var g in tierSkips) u.Suppress(g);
                if (t.Part == anchor)
                {
                    if (pipe.Vb1Variants.Count > 0)
                        u.Capture($"${Vb1Var(sfx)} = {pipe.TierVb1.GetValueOrDefault(t.Name)}");
                    var chain = new List<string>();
                    PosePasses? pose = null;
                    if (pipe.PoseRoute is { } route)
                    {
                        // this level's own gather and palette pass; where this tier recovers nothing
                        // itself, the lod0 packet gathered here out of the lod0 capture reference, which
                        // the game refreshes at frame start, and the lod0's palette pass on it. Placed as
                        // the lod0 walk places its passes: with the draw, wherever the draw goes.
                        string kernelMesh = route.TierKernel[t.Name];
                        pose = PosePassesFor(route, kernelMesh == t.Name ? $"CustomShaderGather_{t.Name}"
                            : $"CustomShaderGatherRef_{kernelMesh}", kernelMesh, sfx, poseBlocks);
                    }
                    else
                    {
                        chain.Add($"if $zz_done_{sfx}_{t.Suffix} == 0");
                        for (int pi = 0; pi < pipe.PartMeta.Count; pi++)
                        {
                            string p2 = pipe.PartMeta[pi].Part;
                            var pt = pipe.TierMeta.FirstOrDefault(x => x.Part == p2 && x.Suffix == t.Suffix
                                && x.Rows > 0);
                            RecoverRun(chain, pipe, pi, pt.Name ?? p2, sfx);
                        }
                        chain.Add($"run = CustomShaderConvertW_{sfx}");
                        MemberRuns(chain, pipe, sfx);
                        if (pipe.TierTies.TryGetValue(t.Suffix, out int tierPairs) && tierPairs > 0)
                            chain.Add($"run = CustomShaderTierTie_{t.Suffix}_{sfx}");
                        TieRuns(chain, pipe, sfx);
                        chain.Add($"run = CustomShaderSkin_{sfx}");
                        chain.Add($"$zz_done_{sfx}_{t.Suffix} = 1");
                        chain.Add("endif");
                    }
                    // the tier's own shapes carry the draw; the map and the ANCHOR's shapes say which of
                    // those shapes each donor range belongs at, and which ranges this tier draws at all
                    var tierRouted = t.Shapes is null ? null
                        : new RoutedDraw(sfx, t.Shapes, drawGate, pipe.Draws.Count,
                            AnchorShapes: pipe.AnchorShapes, Map: t.Map, Pose: pose);
                    if (RoutedShapes(t.Shapes, t.Hash, tierRouted) is not null)
                        u.RoutedDraws.Add(tierRouted!);
                    else
                    {
                        if (pose is not null) chain.Add(pose.Block(t.Name));
                        chain.Add($"run = CommandListDraw_{sfx}");
                    }
                    if (chain.Count > 0)
                        foreach (var line in drawGate.Wrap(chain)) u.Run(line);
                }
            }

            // wardrobe-group members: the member's own draw captures its posed vertices and latches its
            // presence; the fused dispatch runs in the anchor's chains, gated on LAST frame's latch. The
            // section is the one this hash already owns where another pipeline pools the same mesh — a
            // second unfiltered section would fire beside it at every draw — and the member NEVER adds a
            // skip of its own: an unworn variant issues no draws, so its latch clears and the chain stops
            // dispatching it. Only an AT-DRAW fallback (lod0 with no anchor witness) still runs here,
            // where its constants copy and its geometry are same-frame by construction.
            foreach (var m in pipe.GroupMembers)
            {
                var u = Unit(m.Hash, $"Cap_{m.Name}");
                u.Capture($"Resource_{m.Name}_Posed = ref vb0");
                if (m.Lod0 && m.AtDraw) u.Capture($"Resource_{m.Name}_CB = copy vs-cb1");
                if (!m.AtDraw) continue;   // presence latch lands via RouteSightings; no run lines
                // Inside the pipeline's draw gate, exactly as the pool chains are: off dispatches nothing,
                // and a dispatch left running with the key off would keep writing the group's palette rows.
                var chain = new List<string>
                {
                    $"if ${GroupCbVar(sfx)} == 1",
                    $"run = CustomShaderGroup_{m.Name}_{sfx}",
                    "endif",
                };
                foreach (var line in drawGate.Wrap(chain)) u.Run(line);
            }

            // A hidden member's suppression, on every mesh the build claimed for it rather than on the ones
            // that kept a dispatch: a mesh dropped above (no lod0, no witness bone, an all-sentinel map) is
            // still claimed, so the hide pass has already left it alone and this section is the only place
            // left that can skip it. The capture section is where a captured mesh's suppression has always
            // lived; a mesh the loop above reached finds its unit by hash, and the gate dedupes.
            //
            // A member hidden in EVERY state rides this pipeline's own skip gate. A member a key group
            // hides in some of its positions takes one guarded skip per position instead, and keeps drawing
            // in the rest — the same per-position account a pool part's SuppressWhen entry emits.
            foreach (var c in pipe.GroupClaims)
            {
                if (c.Hidden) Unit(c.Hash, $"Cap_{c.Name}").Suppress(skipGate);
                else
                    foreach (var g in SuppressGates(c.HiddenWhen, modKey, latchVars))
                        Unit(c.Hash, $"Cap_{c.Name}").Suppress(g);
            }
        }
        // each ring is written once per draw of its mesh, while any pipeline reading it is on: one pipeline
        // off leaves another's ring running, and every pipeline off leaves nothing running at all
        foreach (var u in ordered)
            foreach (var (mesh, gates) in u.Rings)
                u.Capture(string.Join("\n", WrapAny(gates, RingRuns(mesh))));
        return new CaptureUnits(ordered, byHash, poseBlocks);
    }

    /// <summary>Where each presence latch's sighting assignment lands. A witness ib whose hash already owns
    /// a TextureOverride records the sighting INSIDE that section; only an unclaimed hash mints a
    /// <c>[TextureOverride_Witness_*]</c> of its own. The runtime would run a second section beside the
    /// owner at every fire, so the sighting rides the owner instead of minting a redundant twin.</summary>
    sealed class Sightings
    {
        /// <summary>ib hash → the assignment lines the hide or scoped-anchor section owning it carries.</summary>
        public readonly Dictionary<string, List<string>> ByHash = new(StringComparer.Ordinal);

        /// <summary>The latch and witness index of every ib no other section claims, each with the twin
        /// sightings that landed on the same ib — one hash owns one section, whichever writer reached
        /// it first.</summary>
        public readonly List<(WitnessLatch Latch, int Index, List<string> Extra)> Standalone = new();

        /// <summary>The twin sightings whose ib no other section carries, in first-seen order: each hash
        /// mints one section holding every line routed to it.</summary>
        public readonly List<(string Hash, List<string> Lines)> Minted = new();
    }

    /// <summary>Route every latch's witness sightings and every twin sighting to their owning sections.
    /// Capture sections take the assignment straight away; a hide, scoped anchor or rigid replacement gets
    /// it back by hash. A capture section under a twin guard takes it into its sighting list instead, which
    /// the emission writes ahead of the guard — a twin sighting always goes there, since the sticky verdict
    /// it writes is what the guard on that section would be testing.</summary>
    static Sightings RouteSightings(IReadOnlyList<WitnessLatch>? latches, CaptureUnits units,
        IReadOnlyList<string> hides, IReadOnlyList<ScopedRetexEntry>? scoped,
        IEnumerable<string>? rigidHashes = null, IReadOnlySet<string>? guardedHashes = null,
        IReadOnlyList<TwinSighting>? twins = null, IReadOnlyList<StockRampBind>? stockRamps = null,
        IReadOnlyList<StockDrawSite>? stockDraws = null)
    {
        var s = new Sightings();
        var hideSet = new HashSet<string>(hides, StringComparer.Ordinal);
        hideSet.UnionWith(rigidHashes ?? Array.Empty<string>());
        // a stock ramp pick or shading change owns its mesh's section exactly as a scoped retexture anchor
        // does, so a sighting on that hash lands INSIDE it rather than minting a witness section of its own
        var anchors = new HashSet<string>((scoped ?? Array.Empty<ScopedRetexEntry>())
            .SelectMany(e => e.Images).SelectMany(i => i.Anchors).Select(a => a.Hash), StringComparer.Ordinal);
        anchors.UnionWith((stockRamps ?? Array.Empty<StockRampBind>()).Select(b => b.IbHash));
        anchors.UnionWith((stockDraws ?? Array.Empty<StockDrawSite>()).Select(site => site.IbHash));
        foreach (var l in latches ?? Array.Empty<WitnessLatch>())
            for (int i = 0; i < l.WitnessIbs.Count; i++)
            {
                string ib = l.WitnessIbs[i], line = $"${SeenVar(l.Name)} = 1";
                if (units.ByHash.TryGetValue(ib, out var u))
                {
                    // An OUTFIT latch on a guarded hash sights AHEAD of the twin guard — either
                    // sibling's draw proves the outfit is on screen. A MESH latch (the src_ family)
                    // witnesses the same event as the capture it gates, so it sights INSIDE the
                    // guard: a sibling's draw captures nothing and must not read as presence.
                    if (guardedHashes?.Contains(ib) == true
                        && !l.Name.StartsWith("src_", StringComparison.Ordinal)) u.Sight(line);
                    else u.Capture(line);
                }
                else if (hideSet.Contains(ib) || anchors.Contains(ib))
                {
                    if (!s.ByHash.TryGetValue(ib, out var lines)) s.ByHash[ib] = lines = new List<string>();
                    if (!lines.Contains(line, StringComparer.Ordinal)) lines.Add(line);
                }
                else s.Standalone.Add((l, i, new List<string>()));
            }
        // routed after the latches, so a mesh a latch already mints a section on carries the twin
        // sighting there rather than under a second override on one hash
        foreach (var t in twins ?? Array.Empty<TwinSighting>())
        {
            string line = $"${t.Var} = {t.Verdict}";
            if (units.ByHash.TryGetValue(t.Hash, out var u)) { u.Sight(line); continue; }
            if (hideSet.Contains(t.Hash) || anchors.Contains(t.Hash))
            {
                if (!s.ByHash.TryGetValue(t.Hash, out var lines)) s.ByHash[t.Hash] = lines = new List<string>();
                if (!lines.Contains(line, StringComparer.Ordinal)) lines.Add(line);
                continue;
            }
            int at = s.Standalone.FindIndex(w =>
                string.Equals(w.Latch.WitnessIbs[w.Index], t.Hash, StringComparison.Ordinal));
            var target = at >= 0 ? s.Standalone[at].Extra : MintedLines(s, t.Hash);
            if (!target.Contains(line, StringComparer.Ordinal)) target.Add(line);
        }
        return s;

        static List<string> MintedLines(Sightings s, string hash)
        {
            int at = s.Minted.FindIndex(m => string.Equals(m.Hash, hash, StringComparison.Ordinal));
            if (at >= 0) return s.Minted[at].Lines;
            var lines = new List<string>();
            s.Minted.Add((hash, lines));
            return lines;
        }
    }

    /// <summary>The sighting sections for the ibs nothing else claims. A latch's index in the name is the
    /// witness's own position in its latch, so a claimed sibling leaves a gap rather than renaming the
    /// sections around it; a twin sighting's section is named for the hash it keys on.</summary>
    static string WitnessIni(Sightings sightings)
    {
        var P = new StringBuilder();
        foreach (var (l, i, extra) in sightings.Standalone)
        {
            OpenTextureOverride(P, $"Witness_{l.Name}_{i}", l.WitnessIbs[i]);
            P.Append($"${SeenVar(l.Name)} = 1\n");
            foreach (var line in extra) P.Append(line).Append('\n');
            P.Append('\n');
        }
        foreach (var (hash, lines) in sightings.Minted)
        {
            OpenTextureOverride(P, $"TwinWit_{hash}", hash);
            foreach (var line in lines) P.Append(line).Append('\n');
            P.Append('\n');
        }
        return P.ToString();
    }

    /// <summary>The sightings a guard of this build reads, deduped by hash, variable and verdict. A write
    /// into a variable no emitted guard tests would identify a sibling no section asks about, so it is
    /// left out — the same discipline that keeps a guard off a key no section carries.</summary>
    static List<TwinSighting> LiveSightings(IReadOnlyList<TwinSighting>? sightings,
        IEnumerable<TwinGuard> guards)
    {
        var vars = new HashSet<string>(guards.Select(g => g.Var), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var live = new List<TwinSighting>();
        foreach (var t in sightings ?? Array.Empty<TwinSighting>())
            if (vars.Contains(t.Var) && seen.Add($"{t.Hash}|{t.Var}|{t.Verdict}")) live.Add(t);
        return live;
    }

    /// <summary>Refuse two stock textures of one build whose derived <see cref="RetexTag"/> collide (a
    /// hash remainder; ~1 in 15e6 pairs). The probes compare tag VALUES, so a shared one binds whichever
    /// replacement the sections order last at the other's slot. The fix line names the kinds the colliding
    /// pair came from — a pair of slot tags has no retexture to drop — and each hash's part label, so the
    /// refusal names change-list rows the author can find.
    ///
    /// <para><paramref name="twinTags"/> are the stock textures a twin guard mints a tag section on. They
    /// carry no part label, and they walk here for the same reason the others do: the guard probes compare
    /// tag VALUES, so a derived value shared with a slot tag or a scoped tag would identify the wrong
    /// sibling.</para></summary>
    internal static void RefuseTagCollisions(IEnumerable<(string Hash, string Part)> slotTags,
        IEnumerable<(string Hash, string Part)> retexes,
        IEnumerable<(string Hash, string Part)>? twinTags = null,
        IEnumerable<(string Hash, string Part)>? bufferTags = null)
    {
        // enumerated in arrival order, so a refusal reads the same way twice
        var retexInOrder = retexes.ToList();
        var retex = new HashSet<string>(retexInOrder.Select(r => r.Hash), StringComparer.OrdinalIgnoreCase);
        var buffers = new HashSet<string>((bufferTags ?? Array.Empty<(string, string)>()).Select(b => b.Hash),
            StringComparer.OrdinalIgnoreCase);
        var byTag = new Dictionary<int, (string Hash, string Part)>();
        // deduped by HASH alone: one texture reaching the build twice is one texture
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var one in slotTags.Concat(retexInOrder)
                     .Concat(twinTags ?? Array.Empty<(string, string)>())
                     .Concat(bufferTags ?? Array.Empty<(string, string)>()))
        {
            if (!seen.Add(one.Hash)) continue;
            int tag = RetexTag(one.Hash);
            if (byTag.TryGetValue(tag, out var other))
            {
                bool otherIsBuffer = buffers.Contains(other.Hash), oneIsBuffer = buffers.Contains(one.Hash);
                throw new AuthoredRefusalException(otherIsBuffer || oneIsBuffer
                    ? $"{Kind(other, otherIsBuffer)} and {Kind(one, oneIsBuffer)} derive the same draw tag ({tag}). "
                        + "The draw predicates can't tell the two apart. "
                        + BufferTagCollisionFix(other, otherIsBuffer, one, oneIsBuffer, retex)
                    : $"Stock textures {Named(other)} and {Named(one)} derive the same slot tag ({tag}). "
                        + "The draw probes can't tell the two apart. "
                        + TagCollisionFix(retex.Contains(other.Hash), other.Part, retex.Contains(one.Hash), one.Part));
            }
            byTag[tag] = one;
        }

        static string Named((string Hash, string Part) t) =>
            t.Part.Length > 0 ? $"{t.Hash} on {t.Part}" : t.Hash;

        static string Kind((string Hash, string Part) t, bool isBuffer) => isBuffer
            ? (t.Part.Length > 0 ? $"the mesh {t.Part}" : $"a mesh buffer ({t.Hash})")
            : $"stock texture {Named(t)}";

        // the buffer side is a mesh edit row; the other side is whatever kind it is
        static string BufferTagCollisionFix((string Hash, string Part) first, bool firstIsBuffer,
            (string Hash, string Part) second, bool secondIsBuffer, HashSet<string> retex)
        {
            if (firstIsBuffer && secondIsBuffer)
                return first.Part.Length > 0 && second.Part.Length > 0
                    ? $"Leave the edit on {first.Part} or the edit on {second.Part} out of the build."
                    : "Leave one of the two mesh edits out of the build.";
            var (buffer, texture) = firstIsBuffer ? (first, second) : (second, first);
            string bufferRow = buffer.Part.Length > 0 ? $"the edit on {buffer.Part}" : "the mesh edit";
            string textureRow = retex.Contains(texture.Hash) ? RetexRow(texture.Part) : MeshRow(texture.Part);
            return $"Leave {bufferRow} or {textureRow} out of the build.";
        }
    }

    /// <summary>The vertex-slot hashes every compound mesh section key in <paramref name="keys"/> will
    /// tag, each named by the part its key labels (<see cref="PoolBuildRequest.MeshLabels"/>) or by its ib
    /// hash. Walked with the texture tags so a derived draw tag can never alias a slot tag.</summary>
    static IEnumerable<(string Hash, string Part)> BufferTags(IReadOnlyDictionary<string, string>? labels,
        IEnumerable<string> keys) =>
        keys.Distinct(StringComparer.Ordinal).Select(DrawSelector.Parse).Where(s => s.IsCompound)
            .SelectMany(s => s.SlotBindings()
                .Select(b => (b.Hash, Part: labels?.GetValueOrDefault(s.Key) ?? s.Hash)));

    /// <summary>What the author can do about one tag collision, by what the colliding pair came from —
    /// named in the change list's own row vocabulary.</summary>
    static string TagCollisionFix(bool firstIsRetex, string firstPart, bool secondIsRetex, string secondPart) =>
        firstIsRetex && secondIsRetex ? "Leave one row's new textures out of the build."
        : firstIsRetex
            ? $"Leave {RetexRow(firstPart)} or {MeshRow(secondPart)} out of the build."
        : secondIsRetex
            ? $"Leave {RetexRow(secondPart)} or {MeshRow(firstPart)} out of the build."
        : "Leave a row with a new mesh out of the build.";

    /// <summary>The retextured row as the fix line names it, falling back to the unlabelled form.</summary>
    static string RetexRow(string part) =>
        part.Length > 0 ? $"the new textures on {part}" : "one row's new textures";

    /// <summary>The replaced row as the fix line names it, falling back to the unlabelled form.</summary>
    static string MeshRow(string part) =>
        part.Length > 0 ? $"the new mesh on {part}" : "a row with a new mesh";

    /// <summary>One rigid replacement as the ini needs it: the sections it owns, the strides and index
    /// format its shipped buffers declare, its donor submesh draws and their texture asks.</summary>
    sealed record RigidEmission(string Sfx, IReadOnlyList<string> Hashes, int Vb0Stride, int? Vb1Stride,
        string IbFmt, List<(int Count, int Start, int Base)> Draws, SubmeshMaps?[] SubMaps,
        KeyRef? ToggleKey, string? Latch, bool HideWhenOff, string? HiddenBy, string? ShownBy,
        IReadOnlyList<KeyRef>? SuppressWhen,
        IReadOnlyDictionary<string, DrawShapeSet>? ShapesByHash,
        IReadOnlyDictionary<string, TierMaterialMap>? MapsByHash,
        IReadOnlyList<StreamVariant> Vb0Variants, IReadOnlyDictionary<string, int> TierVb0,
        IReadOnlyList<StreamVariant> Vb1Variants, IReadOnlyDictionary<string, int> TierVb1, string Part)
    {
        /// <summary>The vertex and index binds of this replacement's draw lists: each slot's primary
        /// buffer, then the variant the drawing section named. vb3 carries stream 0 as vb0 does. A
        /// replacement shipping no variant emits the plain binds.</summary>
        public string Binds()
        {
            var b = new StringBuilder();
            void Slot(string slot, string res, int stream, IReadOnlyList<StreamVariant> variants)
            {
                b.Append($"{slot} = Resource_{res}_{Sfx}\n");
                for (int k = 1; k <= variants.Count; k++)
                    b.Append($"if ${RigidStreamVar(Sfx, stream)} == {k}\n{slot} = Resource_{res}_{Sfx}_v{k}\nendif\n");
            }
            Slot("vb0", "RigidVB0", 0, Vb0Variants);
            if (Vb1Stride is not null) Slot("vb1", "RigidVB1", 1, Vb1Variants);
            Slot("vb3", "RigidVB0", 0, Vb0Variants);
            return b.Append($"ib = Resource_RigidIB_{Sfx}\n").ToString();
        }

        /// <summary>This replacement routes its donor draw per submesh on <paramref name="hash"/>: the
        /// hash's shapes have several drawable submeshes, or its map leaves a donor range with nowhere to
        /// draw. A twin-guarded hash never routes — its draw must stay inside the guard's verdict. The ONE
        /// place the rigid routing condition lives; the section emission and the per-range command lists
        /// both ask it, and a disagreement between the two would leave a list unreferenced or missing.</summary>
        public bool RoutesOn(string hash, IReadOnlyDictionary<string, TwinGuard> guards)
        {
            if (ShapesByHash?.GetValueOrDefault(hash) is not { } shapes) return false;
            if (guards.ContainsKey(hash)) return false;
            return shapes.Shapes.Count(sh => sh.Count > 0) > 1
                || RoutedDropsAnyDraw(shapes, ShapesByHash.GetValueOrDefault(Hashes[0]),
                    MapsByHash?.GetValueOrDefault(hash), Draws.Count);
        }

        /// <summary>This replacement's routed draw on <paramref name="hash"/>, or null when the draw stays
        /// in the hash's own section (see <see cref="RoutesOn"/>).</summary>
        public RoutedDraw? RoutedOn(string hash, IReadOnlyDictionary<string, TwinGuard> guards, Gate drawGate) =>
            RoutesOn(hash, guards)
                ? new RoutedDraw(Sfx, ShapesByHash![hash], drawGate, Draws.Count, IsRigid: true,
                    AnchorShapes: ShapesByHash.GetValueOrDefault(Hashes[0]),
                    Map: MapsByHash?.GetValueOrDefault(hash))
                : null;

        /// <summary>Draw-scoped retexture blocks anchored at one of this replacement's hashes, by hash —
        /// the rigid twin of <see cref="CaptureUnit.ScopeLines"/>; the owning section runs them instead of
        /// a second override minting itself on the same hash.</summary>
        public Dictionary<string, List<string>> ScopeLines { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One guard per guarded hash, first entry wins — a section carries one verdict.</summary>
    static Dictionary<string, TwinGuard> GuardsByHash(IReadOnlyList<TwinGuard>? guards)
    {
        var byHash = new Dictionary<string, TwinGuard>(StringComparer.Ordinal);
        foreach (var g in guards ?? Array.Empty<TwinGuard>()) byHash.TryAdd(g.Hash, g);
        return byHash;
    }

    /// <summary>The guard's draw-time probe: each tagged texture found on a probed ps-t slot writes its
    /// sibling's verdict into the guard's variable. Nothing clears the variable, so a pass binding no
    /// tagged texture leaves the last identification standing. Same slot sweep and same scratch as the
    /// scoped-retexture probe.
    ///
    /// <para>A guard carrying no tags writes NOTHING: its variable is written by sightings elsewhere in
    /// the ini, and a slot sweep here would read the slots for an answer no tag can give.</para></summary>
    /// <summary>Open a hash-keyed TextureOverride section. Every one carries an explicit
    /// <c>match_priority</c>: two mods remolding one subject legitimately put sections on the same
    /// draws, and the runtime warns about a duplicate hash unless a priority on either section marks
    /// the overlap deliberate. Zero keeps the section-name ordering an absent priority already gets;
    /// the tag sections carry their shared 100 instead of this.</summary>
    static void OpenTextureOverride(StringBuilder P, string name, string hash) =>
        P.Append($"[TextureOverride_{name}]\nhash = {hash}\nmatch_priority = 0\n");

    /// <summary>The routed donor draw's sections: one per vanilla submesh, each firing only on the game
    /// draw whose start index and index count it names, plus one for a draw covering the whole mesh in
    /// one call. Donor range k draws at vanilla submesh k's fire (ranges past the last submesh join it),
    /// so every range renders under its own material's bound state. All share the owning section's hash;
    /// their names extend its, and equal match_priority runs same-hash sections in name order, so the
    /// owning section's capture/compute always precedes these draws. A pose draw's passes run here, inside
    /// its gate and ahead of its lists, so they run once per draw of the replacement and at no other fire
    /// of the mesh. A vanilla shape no section names
    /// draws nothing — its original is already suppressed — and a full shape colliding with a submesh
    /// shape yields that submesh's section alone (the two draws cannot be told apart).</summary>
    static void EmitRoutedDrawSections(StringBuilder P, string hash, string ownerName,
        IReadOnlyList<RoutedDraw> routedDraws)
    {
        // Every entry describes the one mesh this hash names; disagreement is a caller contract break.
        // Two submeshes covering one index range are one draw the runtime cannot tell
        // apart, so DISTINCT SHAPES — not submesh indices — get sections, and a zero-index-count
        // submesh (a material slot with no geometry) never draws: donor ranges folding onto one land
        // on the last drawable shape instead.
        var shapeSet = routedDraws[0].Shapes;
        if (routedDraws.Skip(1).Any(routed => !SameDrawShapeSet(shapeSet, routed.Shapes)))
            throw new InvalidOperationException(
                $"routed draws on hash '{hash}' disagree on their target draw shapes");
        var shapes = shapeSet.Shapes;
        var groups = new List<(DrawShape Shape, int FirstK, List<int> Ks)>();
        for (int k = 0; k < shapes.Count; k++)
        {
            if (shapes[k].Count == 0) continue;
            int gi = groups.FindIndex(g => g.Shape == shapes[k]);
            if (gi < 0) groups.Add((shapes[k], k, new List<int> { k }));
            else groups[gi].Ks.Add(k);
        }
        var emitted = new List<DrawShape>();
        foreach (var (shape, firstK, ks) in groups)
        {
            var runs = routedDraws
                .Select(r => (Routed: r, Dis: Enumerable.Range(0, r.DonorDraws)
                    .Where(di => ks.Contains(r.TargetPosition(di))).ToList()))
                .Where(x => x.Dis.Count > 0).ToList();
            if (runs.Count == 0) continue;
            OpenTextureOverride(P, $"{ownerName}_DrawS{firstK}", hash);
            P.Append($"match_first_index = {shape.First}\n");
            P.Append($"match_index_count = {shape.Count}\n");
            foreach (var (routed, dis) in runs)
            {
                string listStem = routed.IsRigid ? "CommandListRigid" : "CommandListDraw";
                routed.DrawGate.Open(P);
                if (routed.Pose is { } pose) P.Append(pose.Block($"{ownerName}_DrawS{firstK}", dis.Contains)).Append('\n');
                foreach (int di in dis) P.Append($"run = {listStem}S{di}_{routed.Sfx}\n");
                routed.DrawGate.Close(P);
            }
            P.Append("\n");
            emitted.Add(shape);
        }
        // the whole-mesh shape, unless a draw of that shape already belongs to an emitted section —
        // the two draws cannot be told apart, and the submesh reading wins
        int full = routedDraws[0].Shapes.FullCount;
        if (emitted.Any(s => s.First == 0 && s.Count == full)) return;
        // A pass that draws the whole mesh in one call normally runs the whole donor. Where the map leaves
        // a range with nowhere to draw at this detail level, the whole-donor list would put it back, so the
        // pass runs the carried ranges one list at a time instead — a dropped range draws in no pass here.
        var fullRuns = routedDraws
            .Select(routed => (Routed: routed, Dis: routed.DropsAnyDraw ? routed.CarriedDraws.ToList() : null))
            .Where(x => x.Dis is not { Count: 0 }).ToList();
        if (fullRuns.Count == 0) return;
        OpenTextureOverride(P, $"{ownerName}_DrawFull", hash);
        P.Append("match_first_index = 0\n");
        P.Append($"match_index_count = {full}\n");
        foreach (var (routed, dis) in fullRuns)
        {
            string listStem = routed.IsRigid ? "CommandListRigid" : "CommandListDraw";
            routed.DrawGate.Open(P);
            if (routed.Pose is { } pose) P.Append(pose.Block($"{ownerName}_DrawFull", dis is null ? null : dis.Contains)).Append('\n');
            if (dis is null) P.Append($"run = {listStem}_{routed.Sfx}\n");
            else foreach (int di in dis) P.Append($"run = {listStem}S{di}_{routed.Sfx}\n");
            routed.DrawGate.Close(P);
        }
        P.Append("\n");
    }

    static bool SameDrawShapeSet(DrawShapeSet first, DrawShapeSet second) =>
        first.FullCount == second.FullCount && first.Shapes.SequenceEqual(second.Shapes);

    /// <summary>The material patches of one target material position, sharing one snapshot at every donor
    /// draw that folds onto it: the owning draw list's suffix, the material position, the resolved donor
    /// draw indices, the ini-safe group id, the carrier gate, and the member patches in request order.</summary>
    internal sealed record MaterialPatchGroup(string Sfx, int MaterialPosition,
        IReadOnlyList<int> DonorDraws, string Gid, int FilterIndex, int ConstantBufferSlot,
        int ByteWidth, IReadOnlyList<MaterialPatchEmission> Patches,
    IReadOnlyList<string> Hashes);

    /// <summary>Validate the request's material patches against the target material fold and group them by
    /// target material position. Everything here throws rather than warns: an empty resolved draw set, a
    /// split filter value, or a missing generated shader would each ship a mod that silently renders wrong.</summary>
    static IReadOnlyList<MaterialPatchGroup> MaterialPatchGroups(
        IReadOnlyList<MaterialPatchEmission>? patches, List<PipelineEmission> pipes,
        List<RigidEmission> rigids, string outDir, IReadOnlyList<StockDrawSite>? stockDraws = null)
    {
        if (patches is not { Count: > 0 }) return Array.Empty<MaterialPatchGroup>();
        foreach (var patch in patches)
        {
            if (string.IsNullOrWhiteSpace(patch.Key))
                throw new InvalidOperationException("material patch key is missing");
            if (patch.PixelShaderHashes is not { Count: > 0 })
                throw new InvalidOperationException($"material patch '{patch.Key}' names no candidate pixel shaders");
            if (patch.PixelShaderHashes.Any(hash => hash is not { Length: 16 } || !hash.All(Uri.IsHexDigit)))
                throw new InvalidOperationException($"material patch '{patch.Key}' carries a malformed pixel-shader hash");
        }
        // A patch names a material position of the replaced part as the modder sees it, which is the lod0
        // position. Tiers order their own materials, so their shape sets legitimately differ; each tier's
        // material map is what carries the patched position there, or says the tier draws it nowhere.
        // Neither is a reason to refuse the patch.
        static DrawShapeSet? AnchorPipeShapes(PipelineEmission pipe) => pipe.AnchorShapes;
        // The patch's position is the modder's, read on the replaced part's OWN draw — the first hash,
        // which is the lod0 one. A tier's shapes are not that reading: its materials are ordered on its
        // own, so resolving a patch against them would land it on another material. A caller that supplies
        // no shapes for the own hash has not said where the patch goes, and the caller below refuses.
        static DrawShapeSet? OwnRigidShapes(RigidEmission rigid) =>
            rigid.Hashes.Count > 0 ? rigid.ShapesByHash?.GetValueOrDefault(rigid.Hashes[0]) : null;
        static bool SafeKey(string key) => key.All(c => c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
        // The fold a patch is resolved against is the ANCHOR's (lod0's), which is the one the modder picked
        // the position on. Read lazily, per suffix a patch actually names.
        var draws = pipes.Select(pipe => (pipe.Sfx, Count: pipe.Draws.Count,
                Shapes: (Func<DrawShapeSet?>)(() => AnchorPipeShapes(pipe))))
            .Concat(rigids.Select(rigid => (rigid.Sfx, Count: rigid.Draws.Count,
                Shapes: (Func<DrawShapeSet?>)(() => OwnRigidShapes(rigid)))))
            .ToDictionary(unit => unit.Sfx, StringComparer.Ordinal);
        // An unreplaced part's own draw of one material: its patches run around the game's draw rather than
        // a donor's, so it names no donor draws and exactly one material, its own.
        var sites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in stockDraws ?? Array.Empty<StockDrawSite>())
        {
            if (string.IsNullOrWhiteSpace(site.Id) || !SafeKey(site.Id) || !sites.Add(site.Id))
                throw new InvalidOperationException(
                    $"stock draw '{site.Id}' is missing, repeated or outside its safe alphabet");
            if (draws.ContainsKey(site.Id))
                throw new InvalidOperationException(
                    $"stock draw '{site.Id}' takes the name of a replacement in this build");
            if (site.Shape.First < 0 || site.Shape.Count <= 0)
                throw new InvalidOperationException(
                    $"stock draw '{site.Id}' names draw range {site.Shape.First}+{site.Shape.Count}, which "
                    + "the game never issues");
        }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<(MaterialPatchEmission Patch, IReadOnlyList<int> DonorDraws)>();
        foreach (var patch in patches)
        {
            IReadOnlyList<int> donorDraws;
            if (sites.Contains(patch.Suffix))
            {
                if (patch.Submesh != 0)
                    throw new InvalidOperationException(
                        $"material patch '{patch.Key}' names material position {patch.Submesh} of stock draw "
                        + $"'{patch.Suffix}', which draws one material");
                donorDraws = Array.Empty<int>();
            }
            else
            {
                if (!draws.TryGetValue(patch.Suffix, out var unit))
                    throw new InvalidOperationException(
                        $"material patch '{patch.Key}' names replacement '{patch.Suffix}', which this build does not draw");
                if (patch.Submesh < 0)
                    throw new InvalidOperationException(
                        $"material patch '{patch.Key}' names negative material position {patch.Submesh} "
                        + $"of '{patch.Suffix}'");
                var unitShapes = unit.Shapes()
                    ?? throw new InvalidOperationException(
                        $"material patch '{patch.Key}' cannot resolve the target material positions "
                        + $"of '{patch.Suffix}'");
                donorDraws = Enumerable.Range(0, unit.Count)
                    .Where(draw => DrawMaterialFold.TargetMaterialPosition(unitShapes, draw) == patch.Submesh)
                    .ToArray();
                if (donorDraws.Count == 0)
                    throw new InvalidOperationException(
                        $"material patch '{patch.Key}' names material position {patch.Submesh} of "
                        + $"'{patch.Suffix}', which receives no donor draws");
            }
            if (string.IsNullOrWhiteSpace(patch.Key))
                throw new InvalidOperationException(
                    $"material patch key '{patch.Key}' is missing or repeated");
            if (!SafeKey(patch.Key))
                throw new InvalidOperationException(
                    $"material patch key '{patch.Key}' contains characters outside its safe alphabet");
            if (!keys.Add(patch.Key))
                throw new InvalidOperationException(
                    $"material patch key '{patch.Key}' is missing or repeated");
            bool writes = WritesConstants(patch);
            if (writes && (patch.ConstantBufferSlot is < 0 or > 13))
                throw new InvalidOperationException(
                    $"material patch '{patch.Key}' has constant-buffer slot {patch.ConstantBufferSlot}, outside 0..13");
            if (writes && (patch.ByteWidth <= 0 || patch.ByteWidth % 16 != 0))
                throw new InvalidOperationException(
                    $"material patch '{patch.Key}' has byte width {patch.ByteWidth}, which is not a positive multiple of 16");
            foreach (var write in patch.Writes ?? Array.Empty<MaterialPatchWrite>())
                if (write.ByteOffset < 0 || write.ByteOffset % 4 != 0 || write.ByteOffset >= patch.ByteWidth)
                    throw new InvalidOperationException(
                        $"material patch '{patch.Key}' writes byte {write.ByteOffset}, outside its {patch.ByteWidth}-byte buffer");
            if (patch.FilterIndex is <= 0 or > 16_777_216)
                throw new InvalidOperationException(
                    $"material patch '{patch.Key}' has filter value {patch.FilterIndex}, outside the exact-float range");
            if (patch.PixelShaderHashes is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"material patch '{patch.Key}' names no candidate pixel shaders");
            foreach (var hash in patch.PixelShaderHashes)
            {
                if (hash is not { Length: 16 } || !hash.All(Uri.IsHexDigit))
                    throw new InvalidOperationException(
                        $"material patch '{patch.Key}' carries a malformed pixel-shader hash '{hash}'");
            }
            if (!writes && !patch.IsEffect)
                throw new InvalidOperationException($"material patch '{patch.Key}' writes no values");
            if (!writes && !patch.SkipDraw && patch.TextureOverrides is not { Count: > 0 })
                throw new InvalidOperationException($"material effect '{patch.Key}' has no operation");
            if (patch.SkipDraw && (writes || patch.TextureOverrides is { Count: > 0 }))
                throw new InvalidOperationException($"material effect '{patch.Key}' mixes draw omission and resource writes");
            foreach (var texture in patch.TextureOverrides ?? Array.Empty<MaterialEffectTexture>())
            {
                if (texture.Slot is < 0 or > 127 || new[] { texture.R, texture.G, texture.B, texture.A }
                    .Any(value => !float.IsFinite(value) || value is not (0 or 1)))
                    throw new InvalidOperationException($"material effect '{patch.Key}' has an invalid neutral texture");
                string file = Path.Combine(outDir, EffectTextureFile(texture));
                if (!File.Exists(file)) FlatDds.Write(file, ((byte)(texture.R * 255), (byte)(texture.G * 255),
                    (byte)(texture.B * 255), (byte)(texture.A * 255)), srgb: false);
            }
            resolved.Add((patch, donorDraws));
        }
        // One program carries one filter value. Programs are classed by the exact set of patches that
        // reach them: a value edit reaches its whole candidate family, an effect operation the programs
        // it is proven for. A family under one recipe stays one group with its own filter value, exactly
        // as a value-only build emits it; a family whose passes take different operations splits only
        // where the operations differ, each class deriving its value from its own sorted programs.
        // Accepted consequence: two mods that patch the same shader family with different effect
        // disables partition it differently and tag a shared program with different values, and the
        // loader keeps one — the other mod's gate then never fires for that program. Dev already had this
        // whenever two mods' families overlapped; the classes add the effect case.
        var keysByHash = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (patch, _) in resolved)
            foreach (string hash in patch.PixelShaderHashes)
            {
                if (!keysByHash.TryGetValue(hash, out var keysOf))
                    keysByHash[hash] = keysOf = new SortedSet<string>(StringComparer.Ordinal);
                keysOf.Add(patch.Key);
            }
        int ClassFilter(IReadOnlyList<string> hashes, HashSet<string> members)
        {
            var owned = resolved.Select(item => item.Patch)
                .Where(patch => !patch.IsEffect && members.Contains(patch.Key)
                    && patch.PixelShaderHashes.Select(hash => hash.ToLowerInvariant())
                        .OrderBy(hash => hash, StringComparer.Ordinal).SequenceEqual(hashes, StringComparer.Ordinal))
                .Select(patch => patch.FilterIndex).Distinct().ToList();
            return owned.Count == 1 ? owned[0] : DerivedMaterialEvidence.FamilyFilterValue(hashes);
        }
        var classes = keysByHash
            .GroupBy(pair => string.Join("\n", pair.Value), pair => pair.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(group =>
            {
                var members = group.Key.Split('\n').ToHashSet(StringComparer.Ordinal);
                IReadOnlyList<string> hashes = group.OrderBy(hash => hash, StringComparer.Ordinal).ToList();
                return (Members: members, Hashes: hashes, Filter: ClassFilter(hashes, members));
            })
            .OrderBy(programClass => programClass.Filter).ToList();
        if (classes.Select(programClass => programClass.Filter).Distinct().Count() != classes.Count)
            throw new InvalidOperationException("two material program classes collide on one filter value");
        var groups = new List<MaterialPatchGroup>();
        foreach (var submesh in resolved.GroupBy(item => (item.Patch.Suffix, item.Patch.Submesh)))
        {
            var touching = classes.Where(programClass =>
                submesh.Any(item => programClass.Members.Contains(item.Patch.Key))).ToList();
            for (int ordinal = 0; ordinal < touching.Count; ordinal++)
            {
                var programClass = touching[ordinal];
                var members = submesh.Where(item => programClass.Members.Contains(item.Patch.Key))
                    .Select(item => item.Patch).ToList();
                var writing = members.Where(WritesConstants).ToList();
                if (writing.Select(patch => patch.ConstantBufferSlot).Distinct().Count() > 1)
                    throw new InvalidOperationException(
                        $"material patches on submesh {submesh.Key.Submesh} of '{submesh.Key.Suffix}' disagree on "
                        + "which draw they bind at. One draw has one bound material");
                if (writing.Select(patch => patch.ByteWidth).Distinct().Count() > 1)
                    throw new InvalidOperationException(
                        $"material patches on submesh {submesh.Key.Submesh} of '{submesh.Key.Suffix}' disagree on "
                        + "their constant-buffer byte width");
                if (members.SelectMany(patch => patch.TextureOverrides ?? Array.Empty<MaterialEffectTexture>())
                    .GroupBy(texture => texture.Slot).Any(slot => slot.Distinct().Count() != 1))
                    throw new InvalidOperationException("material effects disagree on a neutral texture binding");
                string gid = touching.Count == 1
                    ? $"{submesh.Key.Suffix}_s{submesh.Key.Submesh}"
                    : $"{submesh.Key.Suffix}_s{submesh.Key.Submesh}_c{ordinal}";
                groups.Add(new MaterialPatchGroup(submesh.Key.Suffix, submesh.Key.Submesh,
                    submesh.First().DonorDraws, gid, programClass.Filter,
                    writing.FirstOrDefault()?.ConstantBufferSlot ?? -1, writing.FirstOrDefault()?.ByteWidth ?? 0,
                    members, programClass.Hashes));
            }
        }
        return groups;
    }

    static string EffectTextureFile(MaterialEffectTexture texture) =>
        $"effect_neutral_{(int)texture.R}{(int)texture.G}{(int)texture.B}{(int)texture.A}.dds";

    /// <summary>The material patches' section block: one tag per exact pixel program carrying its
    /// class's filter value, and per group the saved bindings, the patch pass and the buffers it writes
    /// through. The draw-site gate is emitted by <see cref="EmitDrawTextures"/> inside every list that
    /// issues the patched draw.</summary>
    static void EmitMaterialPatchSections(StringBuilder P, IReadOnlyList<MaterialPatchGroup> groups)
    {
        if (groups.Count == 0) return;
        var tagged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            foreach (var hash in group.Hashes)
                if (!tagged.TryAdd(hash, group.FilterIndex) && tagged[hash] != group.FilterIndex)
                    throw new InvalidOperationException(
                        $"pixel shader {hash} is tagged with filter values {tagged[hash]} and {group.FilterIndex} — "
                        + "one shader carries one value");
        foreach (var (hash, filter) in tagged.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            P.Append($"\n[ShaderOverride_MaterialPass_{hash}]\nhash = {hash}\n"
                   + $"filter_index = {filter}\nallow_duplicate_hash = true\n");
        foreach (var group in groups)
        {
            foreach (var texture in group.Patches.SelectMany(patch =>
                         patch.TextureOverrides ?? Array.Empty<MaterialEffectTexture>()).Distinct())
                P.Append($"\n[Resource_MaterialTextureSave_{group.Gid}_{texture.Slot}]\n")
                 .Append($"\n[Resource_MaterialTexture_{group.Gid}_{texture.Slot}]\nfilename = {EffectTextureFile(texture)}\n");
            if (!HasPatchShader(group)) continue;
            int elements = group.ByteWidth / 16;
            P.Append($"\n[Resource_MaterialSource_{group.Gid}]\n\n")
             .Append($"[{PatchViewResource(group)}]\ntype = Texture2D\nformat = R32_FLOAT\n")
             .Append($"width = {elements}\nheight = 1\narray = 1\nmips = 1\nmsaa = 1\nbind_flags = render_target\n\n")
             .Append($"[{PatchTargetResource(group)}]\ntype = Buffer\nformat = R32G32B32A32_UINT\n")
             .Append($"array = {elements}\nbind_flags = render_target\n\n")
             .Append($"[Resource_MaterialDraw_{group.Gid}]\ntype = Buffer\n")
             .Append($"byte_width = {group.ByteWidth}\nstride = 0\nbind_flags = constant_buffer\n");
            P.Append($"\n[{PatchShaderSection(group)}]\n").Append(PatchPassState)
             .Append($"ps = {PatchPassFile(group)}\n")
             .Append($"o0 = set_viewport {PatchViewResource(group)}\no0 = {PatchTargetResource(group)}\ndraw = 3, 0\n");
        }
    }

    /// <summary>The pixel pass that patches one group's constants at a draw. A value edit whose programs span
    /// two program classes belongs to both classes' groups, and each group patches its own buffers, so the
    /// pass is named per group: one name for both would keep only the first, and the second class's draws
    /// would run a pass writing the first group's buffers.</summary>
    static string PatchShaderSection(MaterialPatchGroup group) => $"CustomShader_MaterialPatch_{group.Gid}";
    static string PatchPassFile(MaterialPatchGroup group) => $"generated/material_pass_{group.Gid}.hlsl";
    /// <summary>The texture whose size the patch pass takes its viewport from, one pixel per 16-byte element
    /// of the constants: <c>set_viewport</c> reads a texture's size and ignores a buffer's.</summary>
    static string PatchViewResource(MaterialPatchGroup group) => $"Resource_MaterialView_{group.Gid}";
    /// <summary>The buffer the patch pass writes, one element per 16 bytes of the constants, typed as integers
    /// so every byte arrives as the game or the patch wrote it.</summary>
    static string PatchTargetResource(MaterialPatchGroup group) => $"Resource_MaterialTarget_{group.Gid}";
    /// <summary>The state the patch pass sets: the fullscreen triangle, no other stages, no culling, depth or
    /// blending, the depth target and the other colour targets cleared (the game's own targets are still
    /// bound, and a target set whose members differ in size is dropped). The loader restores all of it when
    /// the pass ends.</summary>
    const string PatchPassState = $"vs = {PoseFullscreenFile}\nhs = null\nds = null\ngs = null\ntopology = triangle_list\n"
        + "cull = none\ndepth_enable = false\nblend = disable\nod = null\no1 = null\no2 = null\no3 = null\no4 = null\n"
        + "o5 = null\no6 = null\no7 = null\n";

    /// <summary>The lines that patch one group's constants at a draw while its program is bound: the patch
    /// pass reads the constants the game bound and writes them with the group's values over theirs, the
    /// result is copied into the constant buffer, and the draw binds that buffer. Every draw patches its own
    /// constants, so two draws of one material that the game gives different constants keep them apart.</summary>
    // Accepted consequence: a loader that failed to run the pass would still bind its target, unwritten, so
    // the draw would render with zeroed or stale constants instead of the material's own. The failure shows
    // on the material rather than passing for a stock draw.
    static IEnumerable<string> PatchLines(MaterialPatchGroup group)
    {
        yield return $"run = {PatchShaderSection(group)}";
        yield return $"Resource_MaterialDraw_{group.Gid} = copy {PatchTargetResource(group)}";
        yield return $"ps-cb{group.ConstantBufferSlot} = Resource_MaterialDraw_{group.Gid}";
    }

    /// <summary>The shaders a mod's patch passes draw with: the fullscreen triangle and, per group, the pixel
    /// shader that copies each 16-byte element of the constants bound at the group's slot and writes the
    /// group's values over its components, every patch in the group's order (a later patch's value at a byte
    /// wins). The constants are read and written as integers, so no byte is rounded on the way.</summary>
    static void WriteMaterialPatchShaders(string outDir, IReadOnlyList<MaterialPatchGroup> groups)
    {
        var shaded = groups.Where(HasPatchShader).ToList();
        if (shaded.Count == 0) return;
        File.WriteAllText(Path.Combine(outDir, PoseFullscreenFile), ComputeTemplates.EmitPoseFullscreen());
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        foreach (var group in shaded)
        {
            var values = new SortedDictionary<int, uint>();
            foreach (var patch in group.Patches.Where(WritesConstants))
                foreach (var write in patch.Writes!)
                    values[write.ByteOffset] = unchecked((uint)BitConverter.SingleToInt32Bits(write.Value));
            var text = new StringBuilder()
                .Append("// One draw's material constants as the game bound them, with this mod's values written over\n")
                .Append("// theirs: one 16-byte element per pixel, copied into the constant buffer the draw binds.\n")
                .Append($"cbuffer material_state : register(b{group.ConstantBufferSlot}) {{ uint4 material_constants[{group.ByteWidth / 16}]; }}\n")
                .Append("uint4 main(float4 pos : SV_Position) : SV_Target {\n")
                .Append("    uint e = (uint)pos.x;\n")
                .Append("    uint4 v = material_constants[e];\n");
            foreach (var element in values.GroupBy(entry => entry.Key / 16))
            {
                text.Append("    if (e == ").Append(element.Key.ToString(CultureInfo.InvariantCulture)).Append("u) {");
                foreach (var (offset, bits) in element)
                    text.Append(" v.").Append("xyzw"[offset % 16 / 4])
                        .Append(" = 0x").Append(bits.ToString("x8", CultureInfo.InvariantCulture)).Append("u;");
                text.Append(" }\n");
            }
            text.Append("    return v;\n}\n");
            File.WriteAllText(Path.Combine(outDir, PatchPassFile(group).Replace('/', Path.DirectorySeparatorChar)),
                text.ToString());
        }
    }

    void AppendTwinProbe(StringBuilder P, TwinGuard guard)
    {
        foreach (var line in TwinProbeLines(guard)) P.Append(line).Append('\n');
    }

    /// <summary>A twin guard's probe as lines: every probed register read once, each tag writing its
    /// sibling's verdict. Nothing for a guard whose verdicts arrive from sightings alone.</summary>
    IEnumerable<string> TwinProbeLines(TwinGuard guard)
    {
        if (guard.Tags.Count == 0) yield break;
        foreach (int s in ProbeSlots)
        {
            yield return $"${VarProbe} = ps-t{s}";
            foreach (var t in guard.Tags)
            {
                yield return $"if ${VarProbe} == {t.TagValue}";
                yield return $"${guard.Var} = {t.Verdict}";
                yield return "endif";
            }
        }
    }

    /// <summary>Opens the guarded body: the section acts while the sticky variable names a mesh it claims.
    /// One claimed verdict opens on the variable itself; several fold into <see cref="VarTwinOk"/> first,
    /// since the ini nests conditions rather than offering an OR. Either shape closes on ONE
    /// <c>endif</c>.</summary>
    static void OpenTwinGuard(StringBuilder P, TwinGuard guard)
    {
        if (guard.OwnVerdicts.Count == 1)
        {
            P.Append($"if ${guard.Var} == {guard.OwnVerdicts[0]}\n");
            return;
        }
        P.Append($"${VarTwinOk} = 0\n");
        foreach (int v in guard.OwnVerdicts)
            P.Append($"if ${guard.Var} == {v}\n${VarTwinOk} = 1\nendif\n");
        P.Append($"if ${VarTwinOk} == 1\n");
    }

    /// <summary>Opens the twin-guard wrap on a section when <paramref name="hash"/> carries a guard —
    /// the probe, then the verdict test — and reports whether it did, so the caller closes with
    /// <see cref="CloseTwinGuard"/>. A guardless hash writes nothing.</summary>
    bool OpenTwinGuardIfAny(StringBuilder P, IReadOnlyDictionary<string, TwinGuard> guards,
        string hash)
    {
        if (!guards.TryGetValue(hash, out var guard)) return false;
        AppendTwinProbe(P, guard);
        OpenTwinGuard(P, guard);
        return true;
    }

    static void CloseTwinGuard(StringBuilder P, bool opened) { if (opened) P.Append("endif\n"); }

    /// <summary>Whether any guard opened around a section admits more than one verdict, so the build
    /// declares <see cref="VarTwinOk"/>. A hide section never opens that way: its skips carry their own
    /// claims' verdicts. False leaves the declarations exactly where a single-verdict build has
    /// them.</summary>
    static bool TwinScratchNeeded(IEnumerable<TwinGuard> guards, IEnumerable<string> hideHashes)
    {
        var hides = hideHashes.ToHashSet(StringComparer.Ordinal);
        return guards.Any(g => g.OwnVerdicts.Count > 1 && !hides.Contains(g.Hash));
    }

    /// <summary>Every sticky variable the emitted guards read, first-seen order. Declared in
    /// <c>[Constants]</c> and written nowhere else, so an unidentified signature reads 0 and the
    /// sections it guards stay inert.</summary>
    static List<string> TwinVars(IEnumerable<TwinGuard> guards) =>
        guards.Select(g => g.Var).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The stock textures a guard probe needs a section of its own on: the ones whose tag value
    /// is derived from the hash rather than carried by a slot tag the build already emits.</summary>
    internal static List<string> MintedTwinTagHashes(IEnumerable<TwinGuard> guards) =>
        guards.SelectMany(g => g.Tags)
            .Where(t => t.TagValue == RetexTag(t.TexHash))
            .Select(t => t.TexHash)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every stock texture a probe needs a tag section of its own on: the twin guards' and the
    /// stock draws' material probes, which derive their values the same way.</summary>
    static List<string> MintedProbeTagHashes(IEnumerable<TwinGuard> guards,
        IEnumerable<StockRampBind>? ramps, IEnumerable<StockDrawSite>? sites) =>
        MintedTwinTagHashes(guards)
            .Concat((ramps ?? Array.Empty<StockRampBind>()).Select(b => b.Material)
                .Concat((sites ?? Array.Empty<StockDrawSite>()).Select(site => site.Material))
                .OfType<MaterialProbe>()
                .Where(probe => probe.TagValue == RetexTag(probe.TexHash))
                .Select(probe => probe.TexHash))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    string EmitIni(List<PipelineEmission> pipes, List<RigidEmission> rigids, CaptureUnits units,
        IReadOnlyList<string> hideHashes,
        Sightings sightings, IReadOnlyList<StockMapTag> slotTags,
        IReadOnlyList<StockPropertyTag> propertyTags,
        IReadOnlySet<string> slimParts, string? modKey,
        IReadOnlyDictionary<string, IReadOnlyList<HideClaim>>? hideClaims,
        IReadOnlyList<RetexEntry> retextures, IReadOnlyList<ScopedRetexEntry>? scopedRetextures = null,
        IReadOnlyList<WitnessLatch>? latches = null,
        IReadOnlyCollection<string>? keysStartingOff = null,
        IReadOnlyDictionary<string, TwinGuard>? twinGuards = null,
        IReadOnlyList<StockRampBind>? stockRamps = null,
        IReadOnlyList<MaterialPatchGroup>? materialPatches = null,
        IReadOnlyList<KeyCycle>? keyCycles = null,
        IReadOnlyList<HiddenFlag>? hiddenFlags = null,
        IReadOnlyDictionary<string, List<string>>? hideScope = null,
        IReadOnlyList<ShownFlag>? shownFlags = null, bool persistModKey = false,
        IReadOnlyList<StockDrawSite>? stockDraws = null, string? appVersion = null,
        IReadOnlyDictionary<string, string>? meshLabels = null, string tail = "")
    {
        var P = new StringBuilder();
        var guards = twinGuards ?? new Dictionary<string, TwinGuard>(StringComparer.Ordinal);
        var patchGroups = materialPatches ?? Array.Empty<MaterialPatchGroup>();

        // every key a change of this build answers to
        var changeKeys = pipes.Select(x => x.ToggleKey?.Key)
                .Concat(rigids.Select(x => x.ToggleKey?.Key))
                .Concat(hideHashes.SelectMany(h => HideClaimKeys(hideClaims, h).Select(k => (string?)k.Key)))
                .Concat(retextures.SelectMany(r => r.Images).Select(i => i.ToggleKey?.Key))
                .Concat((scopedRetextures ?? Array.Empty<ScopedRetexEntry>())
                    .SelectMany(r => r.Images).Select(i => i.ToggleKey?.Key))
                .Concat((stockRamps ?? Array.Empty<StockRampBind>()).Select(b => b.ToggleKey?.Key))
                .Concat((stockDraws ?? Array.Empty<StockDrawSite>()).Select(s => s.ToggleKey?.Key))
                // the states that RAISE a hider flag are keys too: their group may own nothing else in
                // this build, and an undeclared variable would leave every flag stuck at 0
                .Concat((hiddenFlags ?? Array.Empty<HiddenFlag>())
                    .SelectMany(f => f.WhenAny).Select(k => (string?)k.Key))
                // a change gated on a content flag carries no key term of its own, so the key that
                // raises the flag is declared from the flag's own positions or from nowhere
                .Concat((shownFlags ?? Array.Empty<ShownFlag>())
                    .SelectMany(f => f.WhenAny).Select(k => (string?)k.Key))
            .ToList();

        // per-frame compute flags: one per pipeline's lod0 chain, one per anchored tier chain, and on the
        // pooled route one per source mesh, set where its ring is written
        var doneFlags = new List<string>();
        bool pooledRoute = pipes.Any(p => p.PoseRoute?.Sources is not null);
        foreach (var pipe in pipes)
        {
            if (pipe.PoseRoute is not null) continue;   // its passes run at every draw and keep no frame flag
            doneFlags.Add($"zz_done_{pipe.Sfx}");
            string anch = pipe.PartMeta[pipe.AnchorIdx].Part;
            foreach (var tsfx in pipe.TierMeta.Where(t => t.Part == anch).Select(t => t.Suffix).Distinct())
                doneFlags.Add($"zz_done_{pipe.Sfx}_{tsfx}");
        }
        // The stock hashes the scoped retextures tag: their RetexTag section is the probe's answer
        // for those textures, so the slot-tag emission skips them and the draw probe accepts the
        // derived value alongside the kind tags.
        var scopedHashes = (scopedRetextures ?? Array.Empty<ScopedRetexEntry>())
            .Select(e => e.StockHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Sticky per-pipeline global, never reset, so an AT-DRAW member's rebase can tell "no anchor draw
        // yet" from "the anchor drew last frame". Only the CB flag survives — in-chain member dispatches
        // run at the anchor's own draw and need no proof of it.
        var stickyFlags = pipes.Where(p => p.GroupMembers.Any(m => m.AtDraw))
            .Select(p => GroupCbVar(p.Sfx))
            // the stream-1 variant selector: every anchor capture writes it, so it is never reset either
            .Concat(pipes.Where(p => p.Vb1Variants.Count > 0).Select(p => Vb1Var(p.Sfx)))
            .Concat(rigids.Where(r => r.Vb0Variants.Count > 0).Select(r => RigidStreamVar(r.Sfx, 0)))
            .Concat(rigids.Where(r => r.Vb1Variants.Count > 0).Select(r => RigidStreamVar(r.Sfx, 1))).ToList();
        // The pooled route's textures start cleared when the mod loads: every ring slot at frame 0 with no
        // rows, and the frame number at 1, so no slot a previous load or no draw wrote matches a frame. The
        // [Present] pass advances the number once a frame while any pooled pipeline is on.
        var ringMeshes = pipes.SelectMany(p => p.PoseRoute?.Sources ?? Array.Empty<PoseSource>())
            .Select(source => source.Mesh).Distinct(StringComparer.Ordinal).ToList();
        doneFlags.AddRange(ringMeshes.Select(DrewVar));
        // the ring slot counters are never reset: a reload clears the rings, so any slot reads as empty
        stickyFlags.AddRange(ringMeshes.Select(RingSlotVar));
        var poseClears = !pooledRoute ? null
            : ringMeshes.Select(mesh => $"clear = {RingTexture(mesh)}")
                .Prepend($"clear = {FrameResource} 1").ToList();
        var framePresent = !pooledRoute ? null
            : WrapAny(pipes.Where(p => p.PoseRoute?.Sources is not null).Select(p => DrawGateOf(p, modKey)).ToList(),
                FramePresentLines).ToList();
        P.Append(FlagsIni(doneFlags, slotTags, modKey,
            changeKeys,
            latches, sightings, scopedRetextures is { Count: > 0 }, scopedHashes, keysStartingOff,
            TwinVars(guards.Values), TwinScratchNeeded(guards.Values, hideHashes),
            retextures.Select(r => r.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase), stickyFlags,
            pipes.Select(p => p.SubMaps).Concat(rigids.Select(r => r.SubMaps)).Any(RampTexed)
                || stockRamps is { Count: > 0 },
            stockRamps is { Count: > 0 }, keyCycles, hiddenFlags, shownFlags,
            pipes.Select(p => p.SubMaps).Concat(rigids.Select(r => r.SubMaps))
                .Any(m => m.Any(x => x is not null && !x.Blend.IsInherit)), propertyTags,
            persistModKey, poseClears, framePresent));

        // resource declarations: per-pipeline blocks; shared per-part resources declared by the first
        // pipeline that pools the part
        var declaredParts = new HashSet<string>(StringComparer.Ordinal);
        // A mesh's posed reference and constants copy are declared where some section names them: the chain
        // reads both of every part it recovers from and the posed reference of every tier, the self-contained
        // pose route captures the posed reference of the meshes it recovers from, the pooled route only the
        // replaced part's, and only where a lower-detail mesh of its gathers out of it, and a wardrobe member
        // keeps both as before. Neither pose route reads a constants copy.
        var posedRefs = new HashSet<string>(StringComparer.Ordinal);
        var cbRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pipe in pipes)
        {
            string anchorPart = pipe.PartMeta[pipe.AnchorIdx].Part;
            if (pipe.PoseRoute is null)
            {
                foreach (var (part, _, _, rows) in pipe.PartMeta.Where(p => p.Rows > 0)) { posedRefs.Add(part); cbRefs.Add(part); }
                foreach (var t in pipe.TierMeta.Where(t => t.Rows > 0)) posedRefs.Add(t.Name);
            }
            else if (pipe.PoseRoute.Sources is null)
            {
                foreach (var (part, _, _, rows) in pipe.PartMeta.Where(p => p.Rows > 0)) posedRefs.Add(part);
                foreach (var t in pipe.TierMeta.Where(t => t.Rows > 0)) posedRefs.Add(t.Name);
            }
            else if (pipe.PoseRoute.TierKernel.Values.Contains(anchorPart)) posedRefs.Add(anchorPart);
            foreach (var m in pipe.GroupMembers)
            {
                posedRefs.Add(m.Name);
                if (m.Lod0) cbRefs.Add(m.Name);
            }
        }
        // A mesh's SOLVED operator is shared and declared once; a pipeline whose bind reference differs from
        // the mesh's own binds ships a converted copy under its own suffix instead, and a solved operator
        // no pipeline binds was never kept.
        var solvedUsers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pipe in pipes)
            foreach (string mesh in pipe.PartMeta.Where(p => p.Rows > 0).Select(p => p.Part)
                         .Concat(pipe.TierMeta.Where(t => t.Rows > 0).Select(t => t.Name))
                         .Concat(pipe.GroupMembers.Select(m => m.Name)))
                if (!pipe.ConvertedOps.Contains(mesh)) solvedUsers.Add(mesh);
        void DeclareSolved(string mesh)
        {
            if (solvedUsers.Contains(mesh))
                P.Append($"[Resource_{mesh}_Cpinv]\ntype = Buffer\nformat = DXGI_FORMAT_R32_FLOAT\nfilename = {mesh}_cpinv.buf\n");
        }
        var declaredConverted = new HashSet<string>(StringComparer.Ordinal);
        // a packet belongs to its mesh, and pipelines caching one anchor (its toggle states) share it
        var declaredPacketSel = new HashSet<string>(StringComparer.Ordinal);
        void DeclareConverted(PipelineEmission pipe, string mesh)
        {
            if (pipe.ConvertedOps.Contains(mesh) && declaredConverted.Add($"{mesh}|{pipe.Sfx}"))
                P.Append($"[Resource_{mesh}_Cpinv_{pipe.Sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_FLOAT\nfilename = {mesh}_cpinv_{pipe.Sfx}.buf\n");
        }
        foreach (var pipe in pipes)
        {
            string sfx = pipe.Sfx;
            if (pipe.PoseRoute is null)
            {
                P.Append($"[Resource_Palette_{sfx}]\ntype = RWStructuredBuffer\nstride = 16\nfilename = palette_seed_{sfx}.buf\n");
                P.Append($"[Resource_PaletteConv_{sfx}]\ntype = RWStructuredBuffer\nstride = 16\nfilename = palette_seed_{sfx}.buf\n");
                P.Append($"[Resource_OwnerPart_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = owner_part_{sfx}.buf\n");
            }
            foreach (var (part, _, _, rows) in pipe.PartMeta)
            {
                if (rows == 0) continue;
                if (declaredParts.Add(part))
                {
                    DeclareSolved(part);
                    if (slimParts.Contains(part))
                    {
                        P.Append($"[Resource_{part}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_sel.buf\n");
                        P.Append($"[Resource_{part}_Off]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_off.buf\n");
                    }
                    if (posedRefs.Contains(part)) P.Append($"[Resource_{part}_Posed]\n\n");
                    if (cbRefs.Contains(part)) P.Append($"[Resource_{part}_CB]\n\n");
                }
                P.Append($"[Resource_{part}_Map_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_map_{sfx}.buf\n");
                DeclareConverted(pipe, part);
            }
            foreach (var (_, name, _, _, rows, _, _) in pipe.TierMeta)
            {
                if (rows == 0) continue;
                if (declaredParts.Add(name))
                {
                    DeclareSolved(name);
                    if (slimParts.Contains(name))
                    {
                        P.Append($"[Resource_{name}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {name}_sel.buf\n");
                        P.Append($"[Resource_{name}_Off]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {name}_off.buf\n");
                    }
                    if (posedRefs.Contains(name)) P.Append($"[Resource_{name}_Posed]\n\n");
                }
                P.Append($"[Resource_{name}_Map_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {name}_map_{sfx}.buf\n");
                DeclareConverted(pipe, name);
            }
            // wardrobe-group members: the same shared per-mesh declarations a pool part gets (a member this
            // build also pools is declared once), plus this pipeline's own group map
            foreach (var m in pipe.GroupMembers)
            {
                if (declaredParts.Add(m.Name))
                {
                    DeclareSolved(m.Name);
                    if (slimParts.Contains(m.Name))
                    {
                        P.Append($"[Resource_{m.Name}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {m.Name}_sel.buf\n");
                        P.Append($"[Resource_{m.Name}_Off]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {m.Name}_off.buf\n");
                    }
                    // A member's LOD0 carries the same pair a pool part does — one mesh can be reached as a
                    // member here and pooled as a part by another pipeline under the same name, so
                    // whichever route declares it first must leave the other's binds something to name. A
                    // tier's name never meets a pool part's, and no tier binds constants.
                    if (posedRefs.Contains(m.Name)) P.Append($"[Resource_{m.Name}_Posed]\n\n");
                    if (cbRefs.Contains(m.Name)) P.Append($"[Resource_{m.Name}_CB]\n\n");
                }
                P.Append($"[Resource_{m.Name}_GMap_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {m.Name}_gmap_{sfx}.buf\n");
                DeclareConverted(pipe, m.Name);
            }
            if (pipe.PoseRoute is { } route)
            {
                // The skin pass reads the replacement's bind geometry and weights straight from these, so
                // they are declared in the shapes it reads them as: no per-draw copy into a structured view.
                P.Append($"[Resource_NewBind_{sfx}]\ntype = StructuredBuffer\nstride = 40\nfilename = combined_bind_{sfx}.buf\n");
                P.Append($"[Resource_NewSkin_{sfx}]\ntype = StructuredBuffer\nstride = 32\nfilename = combined_skin_{sfx}.buf\n");
                // a slim palette pass's vertex list, remapped to its packet's entries
                foreach (string slimMesh in route.Kernels.Where(k => k.Slim).Select(k => k.Mesh)
                             .Concat((route.Sources ?? Array.Empty<PoseSource>()).Where(s => s.Slim).Select(s => s.Mesh)))
                    if (declaredPacketSel.Add(slimMesh))
                        P.Append($"[Resource_{slimMesh}_PacketSel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\n"
                               + $"filename = {PacketSelFile(slimMesh)}\n");
                // the pooled route's capture of the anchor's rows at each of its draws
                if (route.Sources is not null) P.Append(RowsTexture(AnchorMatResource(sfx)));
                // the palette: one row per pixel, written by the palette pass and read by the skin passes; on
                // the pooled route the row mask beside it, as wide, written by the same passes
                P.Append(RowsTexture($"Resource_PoseTex_{sfx}", route.PaletteRows));
                if (route.Sources is not null) P.Append(RowsTexture(MaskResource(sfx), route.PaletteRows));
                // the viewport every skin pass borrows: set_viewport reads a texture's size and ignores a
                // buffer's, and the viewport it sets stays while the stream buffer is bound as the target
                P.Append($"[Resource_PoseView_{sfx}]\ntype = Texture2D\nformat = R32_FLOAT\n"
                       + $"width = {route.ViewWidth}\nheight = 1\narray = 1\nmips = 1\nmsaa = 1\nbind_flags = render_target\n");
                // per piece: the stream buffer the skin pass writes as a render target, declared typed so
                // it takes the view; its stride-40 alias, which the draw binds (a ref keeps the alias's
                // declared stride); its index buffer, its local-to-donor map and its rows of each UV stream
                foreach (var p in route.Pieces)
                {
                    P.Append($"[Resource_PoseRT_{sfx}_p{p.Number}]\ntype = Buffer\nformat = R32G32B32A32_FLOAT\narray = {p.Elements}\n"
                           + "bind_flags = render_target vertex_buffer\n");
                    P.Append($"[Resource_PoseVB_{sfx}_p{p.Number}]\nstride = 40\n");
                    P.Append($"[Resource_PieceIB_{sfx}_p{p.Number}]\ntype = Buffer\nformat = DXGI_FORMAT_R16_UINT\nfilename = {PieceFile(sfx, p.Number, "ib")}\n");
                    P.Append($"[Resource_PieceMap_{sfx}_p{p.Number}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {PieceFile(sfx, p.Number, "map")}\n");
                    P.Append($"[Resource_PieceVB1_{sfx}_p{p.Number}]\ntype = Buffer\nstride = {pipe.Vb1Stride}\nbind_flags = vertex_buffer\n"
                           + $"filename = {PieceFile(sfx, p.Number, "vb1")}\n");
                    for (int k = 0; k < pipe.Vb1Variants.Count; k++)
                        P.Append($"[Resource_PieceVB1_{sfx}_p{p.Number}_v{k + 1}]\ntype = Buffer\nstride = {pipe.Vb1Variants[k].Stride}\n"
                               + $"bind_flags = vertex_buffer\nfilename = {PieceFile(sfx, p.Number, $"vb1_v{k + 1}")}\n");
                }
                continue;
            }
            P.Append($"[Resource_NewBind_{sfx}]\ntype = RWBuffer\nstride = 40\nfilename = combined_bind_{sfx}.buf\n");
            P.Append($"[Resource_NewSkin_{sfx}]\ntype = RWBuffer\nstride = 32\nfilename = combined_skin_{sfx}.buf\n");
            P.Append($"[Resource_NewVB1_{sfx}]\ntype = RWBuffer\nstride = {pipe.Vb1Stride}\nfilename = combined_vb1_{sfx}.buf\n");
            for (int k = 0; k < pipe.Vb1Variants.Count; k++)
                P.Append($"[Resource_NewVB1_{sfx}_v{k + 1}]\ntype = RWBuffer\nstride = {pipe.Vb1Variants[k].Stride}\n"
                       + $"filename = {pipe.Vb1Variants[k].File}\n");
            P.Append($"[Resource_NewIB_{sfx}]\ntype = Buffer\nformat = {pipe.IbFmt}\nfilename = combined_ib_{sfx}.buf\n");
            // Keep the draw's 40-byte vertex resource separate from the compute shader's stride-zero
            // raw UAV. D3D11's resource/view contracts do not permit one strided resource to serve both
            // roles portably, so the completed raw bytes are copied into the draw buffer.
            P.Append($"[Resource_NewPosed_{sfx}]\ntype = Buffer\nstride = 40\n"
                   + $"bind_flags = vertex_buffer\nfilename = combined_bind_{sfx}.buf\n");
            P.Append($"[Resource_NewPosedUAV_{sfx}]\ntype = RWByteAddressBuffer\nstride = 0\n"
                   + $"bind_flags = unordered_access\nfilename = combined_bind_{sfx}.buf\n");
        }
        // rigid replacements declare only what they bind: the compiled streams, verbatim
        foreach (var r in rigids)
        {
            P.Append($"[Resource_RigidVB0_{r.Sfx}]\ntype = Buffer\nstride = {r.Vb0Stride}\n"
                   + $"filename = rigid_vb0_{r.Sfx}.buf\n");
            if (r.Vb1Stride is { } vb1)
                P.Append($"[Resource_RigidVB1_{r.Sfx}]\ntype = Buffer\nstride = {vb1}\n"
                       + $"filename = rigid_vb1_{r.Sfx}.buf\n");
            for (int k = 0; k < r.Vb0Variants.Count; k++)
                P.Append($"[Resource_RigidVB0_{r.Sfx}_v{k + 1}]\ntype = Buffer\nstride = {r.Vb0Variants[k].Stride}\n"
                       + $"filename = {r.Vb0Variants[k].File}\n");
            for (int k = 0; k < r.Vb1Variants.Count; k++)
                P.Append($"[Resource_RigidVB1_{r.Sfx}_v{k + 1}]\ntype = Buffer\nstride = {r.Vb1Variants[k].Stride}\n"
                       + $"filename = {r.Vb1Variants[k].File}\n");
            P.Append($"[Resource_RigidIB_{r.Sfx}]\ntype = Buffer\nformat = {r.IbFmt}\n"
                   + $"filename = rigid_ib_{r.Sfx}.buf\n");
        }
        P.Append("[Resource_SaveVB0]\n\n[Resource_SaveVB1]\n\n[Resource_SaveVB3]\n\n[Resource_SaveIB]\n\n");
        // the gathers bind their lookup at vs-t1, which the block around them saves and puts back
        if (pipes.Any(p => p.PoseRoute is not null))
        {
            P.Append("[Resource_SaveVST1]\n\n");
            // a ring block binds its slot's number at vs-t2 for its gather
            if (pipes.Any(p => p.PoseRoute?.Sources is not null)) P.Append("[Resource_SaveVST2]\n\n");
            // the pixel-shader slots the pose passes bind their inputs at, saved and put back by hand: a
            // custom shader run restores no shader resources
            foreach (int k in PosePassSlots) P.Append($"[Resource_SavePST{k}]\n\n");
        }
        // the save slots exist for the probe/bind range of each kind of bind this build ships; one that
        // binds nothing never touches a ps-t slot, so it declares none
        var subMapSets = pipes.Select(p => p.SubMaps).Concat(rigids.Select(r => r.SubMaps)).ToList();
        foreach (int s in SavedSlots(subMapSets.Any(DonorTexed), subMapSets.Any(RampTexed),
                     subMapSets.SelectMany(PropertySlots).SelectMany(p => p.Registers)))
            P.Append($"[Resource_SaveT{s}]\n\n");
        if (subMapSets.Any(DonorTexed))
        {
            if (subMapSets.Any(m => UsesNeutral(m, StockMapKind.Normal)))
                P.Append("[Resource_NeutralN]\nfilename = neutral_n.dds\n");
            if (subMapSets.Any(m => UsesNeutral(m, StockMapKind.Rmo)))
                P.Append("[Resource_NeutralRMO]\nfilename = neutral_rmo.dds\n");
        }
        var texRes = new Dictionary<string, string>();
        foreach (var maps in subMapSets)
            foreach (var m in maps)
            foreach (var fn in new[] { m?.Albedo.File, m?.Normal.File, m?.Rmo.File, m?.Ramp.File,
                         m?.Blend.File }.Concat(m?.Properties?.Select(p => p.Map.File)
                         ?? Enumerable.Empty<string?>()))
                if (fn is not null && !texRes.ContainsKey(fn))
                {
                    string name = $"Resource_Tex{texRes.Count}";
                    texRes[fn] = name;
                    P.Append($"[{name}]\nfilename = {fn}\n");
                }
        P.Append("\n");

        // ---- capture units: one section per unique ib hash, merged across pipelines ------------------
        foreach (var u in units.Ordered)
        {
            OpenTextureOverride(P, u.SectionName, u.Hash);
            // The CAPTURE sits inside the guard with the skip and the chain: this hash also fires on a
            // sibling mesh's draws, and a capture taken there would feed palette recovery the wrong
            // rest geometry for every pipeline reading it.
            // a sighting records that the outfit is on screen, which EITHER sibling's draw proves, so it
            // stays outside the guard
            foreach (var line in u.SightingLines) P.Append(line).Append('\n');
            bool guarded = OpenTwinGuardIfAny(P, guards, u.Hash);
            foreach (var line in u.CaptureLines) P.Append(line).Append('\n');
            if (u.Skips)
            {
                // an always-on suppression covers every keyed one, so it emits alone
                if (u.SkipGates.Any(g => g.IsAlwaysOn)) P.Append("handling = skip\n");
                else
                    foreach (var g in CollapseSkips(u.SkipGates, modKey, keyCycles))
                    {
                        g.Open(P);
                        P.Append("handling = skip\n");
                        g.Close(P);
                    }
            }
            foreach (var line in u.RunLines) P.Append(line).Append('\n');
            CloseTwinGuard(P, guarded);
            // the scoped-retexture body carries its own probe and self-corrects, so it stays outside.
            // On a ROUTED hash it moves to a section of its own that sorts after the draw sections:
            // left here it would rebind the slots before the routed draw, and the donor's probe would
            // read the retexture instead of the stock tags.
            if (u.RoutedDraws.Count == 0)
                foreach (var line in u.ScopeLines) P.Append(line).Append('\n');
            P.Append("\n");
            if (u.RoutedDraws.Count > 0)
            {
                EmitRoutedDrawSections(P, u.Hash, u.SectionName, u.RoutedDraws);
                if (u.ScopeLines.Count > 0)
                {
                    OpenTextureOverride(P, $"{u.SectionName}_Scope", u.Hash);
                    foreach (var line in u.ScopeLines) P.Append(line).Append('\n');
                    P.Append("\n");
                }
            }
        }

        // ---- rigid replacements: skip the vanilla draw, draw the donor in its place -------------------
        // Every shipped tier gets the same treatment (LOD choice is not distance-only). Duplicate-named
        // sections drop silently at parse time, and a tier's "_{i}" makes DERIVED names two suffixes can
        // meet on ("t" at tier 1, "t_1" at tier 0), so names are claimed as they are used.
        var usedRigidNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rigids)
        {
            // Suppression and draw gate SEPARATELY — the same split the pooled route makes: sharing one
            // gate returns the vanilla draw when off; dropping the tier-2 key from the suppression gate
            // leaves the part absent.
            var latchVars = LatchTerms(r.Latch);
            var contentVars = With(latchVars, ShownTerm(r.ShownBy));
            var contentGate = new Gate(new KeyRef?[] { ModTerm(modKey), r.ToggleKey }, contentVars);
            var drawGate = r.HiddenBy is null ? contentGate
                : new Gate(new KeyRef?[] { ModTerm(modKey), r.ToggleKey },
                    With(contentVars, HiddenTerm(r.HiddenBy)));
            var skipGate = r.HideWhenOff
                ? new Gate(new KeyRef?[] { ModTerm(modKey) }, latchVars) : contentGate;
            // one guarded skip per extra hiding state, as on the pooled route
            var hiddenSkips = SuppressGates(r.SuppressWhen, modKey, latchVars);
            // no key of its own: the two gates collapse into the single wrapped region of an unkeyed emission
            bool oneGate = hiddenSkips.Count == 0
                && string.Equals(skipGate.Id, drawGate.Id, StringComparison.Ordinal);
            for (int i = 0; i < r.Hashes.Count; i++)
            {
                string name = $"Rigid_{r.Sfx}{(i == 0 ? "" : $"_{i}")}";
                while (!usedRigidNames.Add(name)) name += "_";
                OpenTextureOverride(P, name, r.Hashes[i]);
                // sighting UNGATED, as at hides: a witness silenced while a key was off would read the
                // outfit as absent the frame it comes back on
                if (sightings.ByHash.TryGetValue(r.Hashes[i], out var seen))
                    foreach (var line in seen) P.Append(line).Append('\n');
                // this hash also fires on a sibling mesh's draws, so the suppression and the donor draw
                // wait for the probe to find this part's own tagged texture bound
                // ungated, as the sighting is: it only names which buffers this hash's draw reads
                if (r.Vb0Variants.Count > 0)
                    P.Append($"${RigidStreamVar(r.Sfx, 0)} = {r.TierVb0.GetValueOrDefault(r.Hashes[i])}\n");
                if (r.Vb1Variants.Count > 0)
                    P.Append($"${RigidStreamVar(r.Sfx, 1)} = {r.TierVb1.GetValueOrDefault(r.Hashes[i])}\n");
                bool rigidGuarded = OpenTwinGuardIfAny(P, guards, r.Hashes[i]);
                // a multi-submesh target, or one whose map drops a range, routes its donor draw per submesh
                // (the pooled anchors' rule); a guarded hash keeps the draw here, inside the guard's verdict
                var routedDraw = r.RoutedOn(r.Hashes[i], guards, drawGate);
                if (oneGate)
                {
                    drawGate.Open(P);
                    P.Append(routedDraw is null
                        ? $"handling = skip\nrun = CommandListRigid_{r.Sfx}\n"
                        : "handling = skip\n");
                    drawGate.Close(P);
                }
                else
                {
                    // two wrapped regions rather than a nesting, the shape a pooled capture section emits
                    // when its suppression gate differs from its draw's
                    skipGate.Open(P);
                    P.Append("handling = skip\n");
                    skipGate.Close(P);
                    foreach (var g in hiddenSkips)
                    {
                        g.Open(P);
                        P.Append("handling = skip\n");
                        g.Close(P);
                    }
                    if (routedDraw is null)
                    {
                        drawGate.Open(P);
                        P.Append($"run = CommandListRigid_{r.Sfx}\n");
                        drawGate.Close(P);
                    }
                }
                CloseTwinGuard(P, rigidGuarded);
                // after the suppression and the donor draw, and outside this replacement's gate — the same
                // place a pooled capture section runs its scoped-retexture blocks. On a routed hash the
                // block moves after the draw sections, exactly as at a routed pooled capture.
                bool hasScope = r.ScopeLines.TryGetValue(r.Hashes[i], out var scope);
                if (routedDraw is null && hasScope)
                    foreach (var line in scope!) P.Append(line).Append('\n');
                P.Append("\n");
                if (routedDraw is not null)
                {
                    EmitRoutedDrawSections(P, r.Hashes[i], name, new[] { routedDraw });
                    if (hasScope)
                    {
                        OpenTextureOverride(P, $"{name}_Scope", r.Hashes[i]);
                        foreach (var line in scope!) P.Append(line).Append('\n');
                        P.Append("\n");
                    }
                }
            }
        }

        // hides: a hash captured by any pipeline is never ALSO a hide (refused in Build)
        {
            int hi = 0;
            foreach (var h in hideHashes)
            {
                OpenTextureOverride(P, $"Hide_{hi++}", h);
                // sighting UNGATED: a witness silenced while a key was off would read the outfit as
                // absent the frame it comes back on
                if (sightings.ByHash.TryGetValue(h, out var seen))
                    foreach (var line in seen) P.Append(line).Append('\n');
                // where this hash also fires on a sibling mesh's draws, each skip waits for the probe to
                // find its own hidden mesh's tagged texture bound
                AppendHideSkips(P, guards, hideClaims, h, modKey, keyCycles);
                // a scoped retexture anchored on this same draw, folded in: it carries its own probe and
                // self-corrects, so it sits outside the guard exactly as it does in a capture section
                foreach (var line in HideScopeLines(hideScope, h)) P.Append(line).Append('\n');
                P.Append("\n");
            }
        }

        // the pose blocks the capture units and routed draw sections above run, one per section and pipeline
        foreach (string block in units.PoseBlocks) P.Append(block);
        var declaredGatherRefs = new HashSet<string>(StringComparer.Ordinal);
        // a mesh's packet layout is declared once whether it is gathered as a kernel, as a source or as both;
        // a kernel mesh's packet and plain gather once, and a source mesh's ring once however many pipelines
        // read it
        var declaredLayouts = new HashSet<string>(StringComparer.Ordinal);
        var declaredGathers = new HashSet<string>(StringComparer.Ordinal);
        var declaredRings = new HashSet<string>(StringComparer.Ordinal);
        void DeclareLayout(string mesh)
        {
            if (declaredLayouts.Add(mesh)) P.Append(PacketLayout(mesh));
        }
        void DeclareGather(string mesh, int packet)
        {
            if (declaredGathers.Add(mesh)) P.Append(GatherSection(mesh, packet, withLayout: declaredLayouts.Add(mesh)));
        }
        bool declaredFrame = false;
        foreach (var pipe in pipes)
        {
            string sfx = pipe.Sfx;
            string anchor = pipe.PartMeta[pipe.AnchorIdx].Part;

            if (pipe.PoseRoute is { } route)
            {
                // One palette pass per mesh the anchor is captured from, reading the pose only through the
                // packet its mesh's gather filled at this draw; then one skin pass per piece, reading the
                // palette the pass just wrote. No compute, no unordered-access view: nothing the draw
                // consumes was written through one, so the draw waits on no drain.
                foreach (var (mesh, _, packet, slim) in route.Kernels)
                {
                    DeclareGather(mesh, packet);
                    // a tier recovering nothing gathers this lod0 packet at its own draw, out of the lod0
                    // capture reference
                    if (mesh == anchor && route.TierKernel.Values.Contains(anchor) && declaredGatherRefs.Add(mesh))
                        P.Append(GatherRefSection(mesh, packet));
                    P.Append(PosePaletteSection(pipe, mesh, slim, masked: route.Sources is not null));
                }
                // the pooled route: the anchor's rows captured at its draw, then per source mesh its packet,
                // gather and ring (shared between pipelines), and this pipeline's pick where a bone's row
                // places the source, and its palette pass
                if (route.Sources is { } sources)
                {
                    if (!declaredFrame)
                    {
                        P.Append(FrameSections()).Append(RingSlotSections());
                        declaredFrame = true;
                    }
                    P.Append(AnchorMatSection(sfx));
                    foreach (var source in sources)
                    {
                        DeclareLayout(source.Mesh);
                        if (declaredRings.Add(source.Mesh)) P.Append(RingSections(source.Mesh, source.Packet));
                        if (source.ByBone)
                        {
                            P.Append(RowsTexture(PickResource(source.Mesh, sfx))).Append('\n');
                            P.Append(PosePickSection(sfx, source));
                        }
                        P.Append(PosePaletteSection(pipe, source.Mesh, source.Slim, masked: true, source: source));
                    }
                }
                foreach (var p in route.Pieces) P.Append(PoseSkinSection(pipe, p));
            }
            else
            {
            foreach (var (part, _, _, rows) in pipe.PartMeta.Where(p => p.Rows > 0))
                P.Append($"[CustomShaderRecover_{part}_{sfx}]\ncs = recover_{part}_cs.hlsl\n"
                       + $"cs-u1 = copy Resource_Palette_{sfx}\ncs-t0 = copy Resource_{part}_Posed\n"
                       + $"cs-t1 = {pipe.CpinvResource(part)}\ncs-t2 = Resource_{part}_Map_{sfx}\n"
                       + (slimParts.Contains(part) ? $"cs-t3 = Resource_{part}_Sel\ncs-t4 = Resource_{part}_Off\n" : "")
                       + $"Dispatch = {(rows + 63) / 64}, 1, 1\nResource_Palette_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            foreach (var (_, name, _, _, rows, _, _) in pipe.TierMeta.Where(t => t.Rows > 0))
                P.Append($"[CustomShaderRecover_{name}_{sfx}]\ncs = recover_{name}_cs.hlsl\n"
                       + $"cs-u1 = copy Resource_Palette_{sfx}\ncs-t0 = copy Resource_{name}_Posed\n"
                       + $"cs-t1 = {pipe.CpinvResource(name)}\ncs-t2 = Resource_{name}_Map_{sfx}\n"
                       + (slimParts.Contains(name) ? $"cs-t3 = Resource_{name}_Sel\ncs-t4 = Resource_{name}_Off\n" : "")
                       + $"Dispatch = {(rows + 63) / 64}, 1, 1\nResource_Palette_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            // one LOD level's tier tie: copies the chosen source rows over the level's orphan rows in the
            // converted palette, read through a copy (see the tie underlay's sections below)
            foreach (var (suffix, pairs) in pipe.TierTies.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                P.Append($"[CustomShaderTierTie_{suffix}_{sfx}]\ncs = tiertie_{suffix}_{sfx}.hlsl\n"
                       + $"cs-t0 = copy Resource_PaletteConv_{sfx}\n"
                       + $"cs-u1 = copy Resource_PaletteConv_{sfx}\n"
                       + $"Dispatch = {(4 * pairs + 63) / 64}, 1, 1\n"
                       + $"Resource_PaletteConv_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            // one convert per chunk of parts, each reading the converted palette the chunk before it
            // copied back, so the rows it does not own pass through as that chunk left them.
            // Accepted costs: a pool over one chunk pays the convert's palette copies (raw into t0,
            // converted in and out) once per chunk per frame, 64 bytes per palette slot each (some 14 KB
            // on a real 10-part body pool); and a pipeline whose LOD0 runs the witness convert still ships
            // every chunk's section and shader, as it always shipped the single convert's, so each extra
            // chunk costs one shader compile at load.
            for (int chunk = 0; chunk < ComputeTemplates.ConvertChunks(pipe.PartMeta.Count); chunk++)
            {
                var (first, end) = ComputeTemplates.ConvertChunkParts(pipe.PartMeta.Count, chunk);
                P.Append($"[{ConvertSection(sfx, chunk)}]\ncs = {ConvertFile(sfx, chunk)}\n"
                       + $"cs-u1 = copy Resource_PaletteConv_{sfx}\ncs-t0 = copy Resource_Palette_{sfx}\ncs-t1 = Resource_OwnerPart_{sfx}\n");
                for (int pi = first; pi < end; pi++)
                    if (pipe.PartMeta[pi].Rows > 0)
                        P.Append($"cs-cb{ComputeTemplates.PartRegister(pi)} = Resource_{pipe.PartMeta[pi].Part}_CB\n");
                P.Append($"cs-cb13 = Resource_{anchor}_CB\n"
                       + $"Dispatch = {(4 * pipe.Ub + 63) / 64}, 1, 1\nResource_PaletteConv_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");
            }

            // the witness convert, shared by LOD0 when complete and by every tier chain: K from
            // shared-bone recoveries in the palette's reserved witness slots, no constant buffers
            if (pipe.Lod0WitnessConvert || pipe.TierMeta.Count > 0)
                P.Append($"[CustomShaderConvertW_{sfx}]\ncs = convert_witness_{sfx}.hlsl\n"
                       + $"cs-u1 = copy Resource_PaletteConv_{sfx}\ncs-t0 = copy Resource_Palette_{sfx}\ncs-t1 = Resource_OwnerPart_{sfx}\n"
                       + $"Dispatch = {(4 * pipe.Ub + 63) / 64}, 1, 1\nResource_PaletteConv_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            // the wardrobe-group members' fused recover+rebase — run from the anchor's chains, gated on
            // each mesh's presence latch (an AT-DRAW fallback runs from its own capture section instead).
            // It writes the group's appended slots of the CONVERTED palette directly — the converts
            // dispatch over union rows only, so their own round-trip carries these rows through.
            foreach (var m in pipe.GroupMembers)
            {
                P.Append($"[CustomShaderGroup_{m.Name}_{sfx}]\ncs = grpfuse_{m.Name}_{sfx}.hlsl\n"
                       + $"cs-u1 = copy Resource_PaletteConv_{sfx}\ncs-t0 = copy Resource_{m.Name}_Posed\n"
                       + $"cs-t1 = {pipe.CpinvResource(m.Name)}\ncs-t2 = Resource_{m.Name}_GMap_{sfx}\n"
                       + (slimParts.Contains(m.Name) ? $"cs-t3 = Resource_{m.Name}_Sel\ncs-t4 = Resource_{m.Name}_Off\n" : ""));
                if (m.AtDraw)
                    P.Append($"cs-cb5 = Resource_{m.Name}_CB\ncs-cb13 = Resource_{anchor}_CB\n");
                else
                    P.Append($"cs-t5 = copy Resource_Palette_{sfx}\n");
                P.Append($"Dispatch = {(4 * m.Bones + 63) / 64}, 1, 1\n"
                       + $"Resource_PaletteConv_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");
            }

            // the tie underlay's fill shaders: one per tied part, copying anchor-owned ancestor rows over
            // the absent part's donor-ridden rows in the converted palette
            foreach (var (part, pairs) in pipe.Ties)
                P.Append($"[CustomShaderTie_{part}_{sfx}]\ncs = tiefill_{part}_{sfx}.hlsl\n"
                       + $"cs-t0 = copy Resource_PaletteConv_{sfx}\n"
                       + $"cs-u1 = copy Resource_PaletteConv_{sfx}\n"
                       + $"Dispatch = {(4 * pairs + 63) / 64}, 1, 1\n"
                       + $"Resource_PaletteConv_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            // Skin into a valid raw UAV, unbind it, then copy the bytes into the separate vertex resource.
            // The shader writes every vertex, so the UAV's file seed exists only to establish its size.
            P.Append($"[CustomShaderSkin_{sfx}]\ncs = skin_cs_{sfx}.hlsl\n"
                   + $"cs-u1 = Resource_NewPosedUAV_{sfx}\ncs-t0 = copy Resource_NewBind_{sfx}\n"
                   + $"cs-t1 = copy Resource_NewSkin_{sfx}\ncs-t2 = copy Resource_PaletteConv_{sfx}\n"
                   + $"Dispatch = {(pipe.Vcount + 63) / 64}, 1, 1\ncs-u1 = null\n"
                   + $"Resource_NewPosed_{sfx} = copy Resource_NewPosedUAV_{sfx}\npost cs-u1 = null\n\n");
            }

            // A pose-route pipeline draws each range piece by piece, every piece from the stream buffer
            // its skin pass just wrote; the per-range texture binds wrap all of a range's pieces.
            Func<int, string>? poseDraw = pipe.PoseRoute is null ? null : di => PoseDraw(pipe, di);

            // A COMMAND LIST, not a [CustomShader]: a CustomShader invocation unconditionally
            // saves/restores the viewports and the full OM state (RTVs+UAVs+DSV) around every run — pure
            // per-pass-fire overhead for a section that only rebinds vb/ib/ps-t and draws. Everything it
            // does touch is saved/restored by hand.
            P.Append($"[CommandListDraw_{sfx}]\n"
                   + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
            // texture binds only when some submesh of this pipeline asks for one: a pipeline whose every
            // submesh inherits keeps every original map, so it needs no probe and no ps-t save/restore
            bool donorTexed = DonorTexed(pipe.SubMaps);
            bool rampTexed = RampTexed(pipe.SubMaps);
            var pipePatches = patchGroups.Where(group => group.Sfx == sfx).ToList();
            var restores = TextureRestores(BoundSlotVars(pipe.SubMaps));
            P.Append(DonorBinds(pipe));
            EmitDrawTextures(P, donorTexed, rampTexed, pipe.SubMaps, pipe.Draws, slotTags, propertyTags, texRes,
                pipePatches, poseDraw: poseDraw);
            // vb3 gets its own save: it is rebound to the skin output above, and restoring it from the vb0
            // save would hand the game whatever vb0 held — wrong whenever they differed
            P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
            P.Append(restores);
            // The routed per-range lists: one per donor submesh, the full list's save/bind/restore shape
            // drawing only that range. Referenced by the per-submesh sections a routed capture site emits;
            // a site a twin guard kept on the full list leaves its per-range lists unreferenced and inert.
            if (units.Ordered.Any(u => u.RoutedDraws.Any(rd => !rd.IsRigid && rd.Sfx == sfx)))
                for (int di = 0; di < pipe.Draws.Count; di++)
                {
                    P.Append($"\n[CommandListDrawS{di}_{sfx}]\n"
                           + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
                    P.Append(DonorBinds(pipe));
                    EmitDrawTextures(P, donorTexed, rampTexed, pipe.SubMaps, pipe.Draws, slotTags, propertyTags, texRes,
                        pipePatches, only: di, poseDraw: poseDraw);
                    P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
                    P.Append(restores);
                }
            if (pipes.IndexOf(pipe) + 1 < pipes.Count) P.Append("\n");
        }

        // ---- the rigid draw lists ----------------------------------------------------------------------
        // A pooled draw's command-list shape with the compute chain absent: rebind vb/ib, draw each donor
        // submesh, put the game's own bindings back.
        foreach (var r in rigids)
        {
            if (pipes.Count > 0 || rigids.IndexOf(r) > 0) P.Append("\n");
            P.Append($"[CommandListRigid_{r.Sfx}]\n"
                   + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
            bool rigidTexed = DonorTexed(r.SubMaps);
            bool rigidRamped = RampTexed(r.SubMaps);
            var rigidPatches = patchGroups.Where(group => group.Sfx == r.Sfx).ToList();
            var rigidRestores = TextureRestores(BoundSlotVars(r.SubMaps));
            // vb3 takes the position stream like vb0, matching what a pooled draw binds: the passes that
            // read it read positions.
            P.Append(r.Binds());
            EmitDrawTextures(P, rigidTexed, rigidRamped, r.SubMaps, r.Draws, slotTags, propertyTags, texRes,
                rigidPatches);
            P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
            P.Append(rigidRestores);
            // the rigid twin of the pooled per-range lists above
            if (r.Hashes.Any(h => r.RoutesOn(h, guards)))
                for (int di = 0; di < r.Draws.Count; di++)
                {
                    P.Append($"\n[CommandListRigidS{di}_{r.Sfx}]\n"
                           + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
                    P.Append(r.Binds());
                    EmitDrawTextures(P, rigidTexed, rigidRamped, r.SubMaps, r.Draws, slotTags, propertyTags, texRes,
                        rigidPatches, only: di);
                    P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
                    P.Append(rigidRestores);
                }
        }
        EmitMaterialPatchSections(P, patchGroups);
        P.Append(tail);

        // The ini header: what the mod does and which sections do it, read off the finished file, so it is
        // written last and goes first
        var (replacedParts, hiddenParts) = MeshCounts(pipes, rigids, hideHashes, meshLabels);
        var (slotProbe, materialPasses, retexSections) = IniMarkers(P.ToString());
        P.Insert(0, IniHeader(appVersion, new ModSummary(replacedParts, hiddenParts,
            retextures.Select(r => r.Hash).Concat((scopedRetextures ?? Array.Empty<ScopedRetexEntry>()).Select(e => e.StockHash))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            stockRamps is { Count: > 0 } || patchGroups.Count > 0, modKey,
            StateKeys(modKey, changeKeys, keyCycles))
        {
            Replaces = ReplaceSummaries(pipes),
            Rigids = rigids.Select(r => r.Part).ToList(),
            SlotProbe = slotProbe,
            MaterialPasses = materialPasses,
            Retextures = retexSections,
        }) + "\n");
        return P.ToString();
    }

    /// <summary>The texture half of a replacement's draw list, shared by both routes so a rigid draw and a
    /// pooled one bind identically: the slot probe that reads where the replaced part's own stock maps are
    /// bound right now, then per submesh the binds that row asked for and its drawindexed.
    ///
    /// <para>The ramp is probed and bound apart from the picture maps: it answers a tag of its own over a
    /// range of its own, and a replacement that ships no ramp emits none of it. The binds stay inside this
    /// draw list rather than overriding the ramp resource globally — a global rebind would follow the
    /// texture across every character, material and pass that binds it, and the runtime's texture hash is
    /// too short over this format for distinct ramps to be told apart anyway.</para></summary>
    void EmitDrawTextures(StringBuilder P, bool donorTexed, bool rampTexed, SubmeshMaps?[] subMaps,
        IReadOnlyList<(int Count, int Start, int Base)> draws, IReadOnlyList<StockMapTag> slotTags,
        IReadOnlyList<StockPropertyTag> propertyTags, IReadOnlyDictionary<string, string> texRes,
        IReadOnlyList<MaterialPatchGroup>? patches = null, int only = -1,
        Func<int, string>? poseDraw = null)
    {
        // an ordered list, not a map: the emitted text is a pinned contract, so bind order is fixed
        bool blendTexed = subMaps.Any(m => m is not null && !m.Blend.IsInherit);
        var slotVars = new List<(StockMapKind Kind, string Var)>
        {
            (Kind: StockMapKind.Albedo, Var: VarAlbedoSlot),
            (Kind: StockMapKind.Normal, Var: VarNormalSlot),
            (Kind: StockMapKind.Rmo, Var: VarRmoSlot),
        };
        if (blendTexed) slotVars.Add((StockMapKind.Blend, VarBlendSlot));
        var properties = PropertySlots(subMaps);
        if (donorTexed)
        {
            // Which slot holds each of the anchor's stock maps RIGHT NOW: bound state is final by draw
            // time, so the probe needs no shader table. (A $zz variable written by a PS-keyed section
            // would be one listed draw stale — ShaderOverride lists run VS before PS, and this list fires
            // in the VS phase.) The probe rebinds nothing, so no slot's answer can be an earlier
            // iteration's own assignment. A kind no slot holds keeps -1 and its binds fall through: depth,
            // shadow and outline passes draw geometry-only.
            foreach (var (_, v) in slotVars) P.Append($"${v} = -1\n");
            // A stock map that is ALSO draw-scope retextured (by this mod or any other) answers
            // the probe with its RetexTag, which outranks the kind tag on the same hash. The tag
            // is derived from the hash, so every mod agrees on the value — accepting it per
            // tagged hash keeps the donor binds working under a concurrent scoped retexture.
            string SlotVarFor(StockMapKind k) => k switch
            {
                StockMapKind.Albedo => VarAlbedoSlot,
                StockMapKind.Normal => VarNormalSlot,
                StockMapKind.Blend => VarBlendSlot,
                _ => VarRmoSlot,
            };
            foreach (int s in ProbeSlots)
            {
                P.Append($"${VarProbe} = ps-t{s}\n");
                P.Append($"if ${VarProbe} == {FilterAlbedo}\n${VarAlbedoSlot} = {s}\nendif\n");
                P.Append($"if ${VarProbe} == {FilterNormal}\n${VarNormalSlot} = {s}\nendif\n");
                P.Append($"if ${VarProbe} == {FilterRmo}\n${VarRmoSlot} = {s}\nendif\n");
                if (blendTexed)
                    P.Append($"if ${VarProbe} == {FilterBlend}\n${VarBlendSlot} = {s}\nendif\n");
                // the retexture arm covers the kinds a retexture can reach; a ramp is not one of them, so
                // its tag never stands in for a kind tag here
                foreach (var t in slotTags)
                    if (t.Kind != StockMapKind.Ramp)
                        P.Append($"if ${VarProbe} == {RetexTag(t.Hash)}\n${SlotVarFor(t.Kind)} = {s}\nendif\n");
            }
        }
        var fixedTagKinds = slotTags.GroupBy(t => t.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            string variable = PropertyVar(property.ShaderProperty);
            P.Append($"${variable} = -1\n");
            var accepted = propertyTags.Where(t => t.ShaderProperty == property.ShaderProperty)
                .Select(t => fixedTagKinds.TryGetValue(t.Hash, out var fixedKind)
                    ? KindFilter(fixedKind) : RetexTag(t.Hash)).Distinct().ToList();
            foreach (int s in property.Registers)
            {
                P.Append($"${VarProbe} = ps-t{s}\n");
                foreach (int tag in accepted)
                    P.Append($"if ${VarProbe} == {tag}\n${variable} = {s}\nendif\n");
            }
        }
        if (rampTexed)
        {
            // The ramp's own sweep, over its own candidate registers: only the ramp kind tag is accepted,
            // so a register answering anything else leaves the variable at -1 and the binds fall through
            // to whatever the game bound. A pass with no ramp of its own — depth, shadow, outline — is
            // exactly that case and stays untouched.
            P.Append($"${VarRampSlot} = -1\n");
            foreach (int s in Slots.Ramp)
            {
                P.Append($"${VarProbe} = ps-t{s}\n");
                P.Append($"if ${VarProbe} == {FilterRamp}\n${VarRampSlot} = {s}\nendif\n");
            }
        }
        // Per submesh, per kind: bind what THAT submesh's row asked for, at whichever slot the probe
        // found the anchor's map of that kind in. The list is sequential and one binding outlives the
        // draw that set it, so a slot an earlier submesh bound is put back from its save when a later
        // one inherits — otherwise an untouched submesh would draw wearing its neighbour's map.
        // A single-range list (only >= 0) emits exactly that submesh's binds and draw: alone in its
        // list, it inherits from the game's own binds rather than a neighbour's leftovers.
        // the slots the binds below can touch, saved now that the probes have named them
        P.Append(TextureSaves(BoundSlotVars(subMaps)));
        var bound = new Dictionary<StockMapKind, string?>();   // null/absent = the game's own bind
        void Bind(int di, StockMapKind kind, string slotVar, IReadOnlyList<int> registers)
        {
            var want = Slot(subMaps, di, kind);
            string? res = want.IsNeutral ? NeutralResource(kind)
                : want.File is { } fn ? texRes[fn] : null;
            bound.TryGetValue(kind, out var had);   // absent reads null: still the game's own
            if (string.Equals(had, res, StringComparison.Ordinal)) return;
            bound[kind] = res;
            foreach (int s in registers)
                P.Append($"if ${slotVar} == {s}\nps-t{s} = {res ?? $"Resource_SaveT{s}"}\nendif\n");
        }
        var propertyBound = new Dictionary<string, string?>(StringComparer.Ordinal);
        void BindProperty(int di, PropertyMapSlot property)
        {
            var want = subMaps[di]?.Properties?.FirstOrDefault(p =>
                p.ShaderProperty == property.ShaderProperty)?.Map ?? MapSlot.Inherit;
            string? res = want.File is { } fn ? texRes[fn] : null;
            propertyBound.TryGetValue(property.ShaderProperty, out var had);
            if (string.Equals(had, res, StringComparison.Ordinal)) return;
            propertyBound[property.ShaderProperty] = res;
            string variable = PropertyVar(property.ShaderProperty);
            foreach (int s in property.Registers)
                P.Append($"if ${variable} == {s}\nps-t{s} = {res ?? $"Resource_SaveT{s}"}\nendif\n");
        }
        // 3DMigoto refuses a second `local` declaration of one name in a section scope (an on-screen
        // "Illegal redeclaration" warning and the line is dropped), so a group wrapping several draws
        // in one list declares its gate variable at the first wrap and assigns to it after that.
        var declaredPatchLocals = new HashSet<string>(StringComparer.Ordinal);
        for (int di = only >= 0 ? only : 0; di < (only >= 0 ? only + 1 : draws.Count); di++)
        {
            if (donorTexed)
                foreach (var (kind, slotVar) in slotVars) Bind(di, kind, slotVar, ProbeSlots);
            foreach (var property in properties) BindProperty(di, property);
            if (rampTexed) Bind(di, StockMapKind.Ramp, VarRampSlot, Slots.Ramp);
            // The submesh's material patches, wrapped immediately around its one draw: gate on a
            // exact pixel program being bound (the stable filter value its tag section carries), patch
            // the live constants in one pass (PatchLines), bind the patched copy for this draw alone, and
            // put the game's own resource back — every unowned byte keeps its current runtime value.
            var drawGroups = patches?.Where(candidate => candidate.DonorDraws.Contains(di)).ToArray()
                ?? Array.Empty<MaterialPatchGroup>();
            foreach (var group in drawGroups)
                P.Append(declaredPatchLocals.Add(group.Gid)
                    ? $"local $zz_material_ps_{group.Gid} = ps\n"
                    : $"$zz_material_ps_{group.Gid} = ps\n");
            var skipped = drawGroups.Where(group => group.Patches.Any(patch => patch.SkipDraw)).ToArray();
            foreach (var group in skipped)
                P.Append($"if $zz_material_ps_{group.Gid} != {group.FilterIndex}\n");
            var activeGroups = drawGroups.Where(group => !group.Patches.Any(patch => patch.SkipDraw)).ToArray();
            foreach (var group in activeGroups)
            {
                P.Append($"if $zz_material_ps_{group.Gid} == {group.FilterIndex}\n");
                if (HasPatchShader(group))
                {
                    P.Append($"Resource_MaterialSource_{group.Gid} = ref ps-cb{group.ConstantBufferSlot}\n");
                    foreach (string line in PatchLines(group)) P.Append(line).Append('\n');
                }
                foreach (var texture in group.Patches.SelectMany(patch =>
                             patch.TextureOverrides ?? Array.Empty<MaterialEffectTexture>()).Distinct())
                    P.Append($"Resource_MaterialTextureSave_{group.Gid}_{texture.Slot} = ref ps-t{texture.Slot}\n")
                     .Append($"ps-t{texture.Slot} = Resource_MaterialTexture_{group.Gid}_{texture.Slot}\n");
                P.Append("endif\n");
            }
            P.Append(poseDraw is not null
                ? poseDraw(di)
                : $"drawindexed = {draws[di].Count}, {draws[di].Start}, {draws[di].Base}\n");
            foreach (var group in activeGroups.Reverse())
            {
                P.Append($"if $zz_material_ps_{group.Gid} == {group.FilterIndex}\n");
                foreach (var texture in group.Patches.SelectMany(patch =>
                             patch.TextureOverrides ?? Array.Empty<MaterialEffectTexture>()).Distinct())
                    P.Append($"ps-t{texture.Slot} = ref Resource_MaterialTextureSave_{group.Gid}_{texture.Slot}\n");
                if (group.Patches.Any(WritesConstants))
                    P.Append($"ps-cb{group.ConstantBufferSlot} = Resource_MaterialSource_{group.Gid}\n");
                P.Append("endif\n");
            }
            foreach (var _ in skipped) P.Append("endif\n");
        }
    }

    /// <summary>The retexture sections: one <c>[Resource_Rtx*]</c> per distinct replacement (copied into
    /// <paramref name="outDir"/>) and one <c>[TextureOverride_Retex_*]</c> per entry, keyed on the stock
    /// texture's hash and rebinding it with a gated <c>this =</c> per image the entry carries. No slot
    /// binds, no save/restore — the swap happens where the resource is bound, not around a draw. Two
    /// entries on one hash throw, as do distinct sources sharing a basename (a silent last-copy-wins would
    /// ship the wrong texture).
    ///
    /// <para>An entry whose hash a twin guard also probes for carries that guard's <c>filter_index</c> tag
    /// on its own section, ahead of the gate — one hash owns one section, and the tag has to answer
    /// whether or not the retexture's key is on.</para></summary>
    /// <param name="hideScope">Keyed by every hash this build also HIDES, for the scoped-retexture bodies
    /// whose anchor is one of them. One hash owns one TextureOverride section and the hide's is it, so the
    /// body is handed over exactly as it is to a capture unit's or a rigid replacement's section. Filled
    /// here and emitted by the caller, which writes the hide sections after this has run.</param>
    /// <param name="stockDraws">The unreplaced parts' own material draws the material patches in
    /// <paramref name="patchGroups"/> apply at. Their bodies join the scoped retextures' and the ramp binds'
    /// in the section their mesh owns.</param>
    string RetexIni(IReadOnlyList<RetexEntry> entries, string outDir, string? modKey,
        IReadOnlyList<ScopedRetexEntry>? scoped, CaptureUnits units, Sightings sightings,
        IReadOnlyDictionary<string, RigidEmission>? rigidOwner = null,
        IReadOnlyDictionary<string, TwinGuard>? twinGuards = null,
        IReadOnlyDictionary<string, StockMapKind>? slotTagKinds = null,
        IReadOnlyList<StockRampBind>? stockRamps = null,
        IReadOnlyDictionary<string, List<string>>? hideScope = null,
        IReadOnlyList<StockDrawSite>? stockDraws = null,
        IReadOnlyList<MaterialPatchGroup>? patchGroups = null)
    {
        var P = new StringBuilder();
        var ramps = stockRamps ?? Array.Empty<StockRampBind>();
        var guards = twinGuards ?? new Dictionary<string, TwinGuard>(StringComparer.Ordinal);
        // a site no patch names changes nothing and gets no body
        var groupsBySite = (patchGroups ?? Array.Empty<MaterialPatchGroup>())
            .GroupBy(group => group.Sfx, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var sites = (stockDraws ?? Array.Empty<StockDrawSite>())
            .Where(site => groupsBySite.ContainsKey(site.Id)).ToList();

        // copy + declare each distinct replacement once, before anything assigns it
        var texRes = new Dictionary<string, string>(StringComparer.Ordinal);   // source path → resource name
        var byBasename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void ClaimHash(string name, string hash)
        {
            if (byHash.TryGetValue(hash, out var owner))
                throw new InvalidOperationException(
                    $"retexture entries '{owner}' and '{name}' both override texture hash {hash}");
            byHash[hash] = name;
        }
        void DeclareFile(string ddsFile)
        {
            if (texRes.ContainsKey(ddsFile)) return;
            string bn = Path.GetFileName(ddsFile);
            if (byBasename.TryGetValue(bn, out var other) && !string.Equals(other, ddsFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"retexture textures '{other}' and '{ddsFile}' share the basename '{bn}'. Rename one");
            byBasename[bn] = ddsFile;
            File.Copy(ddsFile, Path.Combine(outDir, bn), overwrite: true);
            texRes[ddsFile] = $"Resource_Rtx{texRes.Count}";
            P.Append($"[{texRes[ddsFile]}]\nfilename = {bn}\n");
        }
        foreach (var e in entries)
        {
            // null as well as empty: Images is a positional record field, so a caller can leave it unset
            // and the named refusal below is what should answer that, not a null dereference
            if (e.Images is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"retexture '{e.Name}' on texture hash {e.Hash} carries no image");
            ClaimHash(e.Name, e.Hash);
            foreach (var img in e.Images) DeclareFile(img.DdsFile);
        }
        foreach (var e in scoped ?? Array.Empty<ScopedRetexEntry>())
        {
            // the same unset-field account as the game-wide guard above
            if (e.Images is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"draw-scoped retexture '{e.Name}' on texture hash {e.StockHash} carries no image");
            if (byHash.TryGetValue(e.StockHash, out var owner))
            {
                if (entries.Any(w => string.Equals(w.Hash, e.StockHash,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"retexture entries '{owner}' and '{e.Name}' both override texture hash {e.StockHash}");
                throw new InvalidOperationException(
                    $"draw-scoped retexture entries '{owner}' and '{e.Name}' both override texture hash "
                    + $"{e.StockHash}; one scoped entry per stock hash is required");
            }
            else byHash[e.StockHash] = e.Name;
            foreach (var img in e.Images) DeclareFile(img.DdsFile);
        }
        foreach (var b in ramps) DeclareFile(b.DdsFile);
        // the scoped sections' ps-t saves, shared across every scoped anchor section
        if (scoped is { Count: > 0 })
            foreach (int s in scoped.SelectMany(e => e.Registers ?? ProbeSlots).Distinct().OrderBy(x => x))
                P.Append($"[Resource_RtxSave{s}]\n");
        // a stock ramp bind saves the ramp's OWN candidate range. The two ranges OVERLAP — the ramp's
        // candidates are measured out of the same shader slot data the probe sweep is — so a register in
        // both is saved under both names. Two names for one register cost a declaration each and nothing
        // else; what matters is that a section carrying both saves and restores each register exactly once,
        // which the anchor bodies below do.
        if (ramps.Count > 0)
            foreach (int s in Slots.Ramp) P.Append($"[Resource_SrSave{s}]\n");
        P.Append("\n");

        // The stock textures a twin guard probes for by a value derived from the hash. A retexture's own
        // section already owns those hashes, so it carries the tag rather than letting a second section
        // mint itself on one — the ini parse drops the second, and which of the two survived could not
        // be predicted.
        var guardList = guards.Values.ToList();
        var mintedTwinTags = MintedProbeTagHashes(guardList, ramps, sites);
        var twinProbed = new HashSet<string>(mintedTwinTags, StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            P.Append($"[TextureOverride_Retex_{e.Name}]\nhash = {e.Hash}\n");
            // A tag rides with the hash and OUTSIDE the gate: the draw probes (and any guard probing
            // this texture) read it whether or not this retexture's key is on. Only the rebind waits on
            // the keys. A slot-tagged hash carries its kind value HERE instead of a SlotTag section of
            // its own — two sections on one hash trip the runtime's mod-conflict warning.
            if (slotTagKinds?.TryGetValue(e.Hash, out var kind) == true)
                P.Append($"filter_index = {KindFilter(kind)}\nmatch_priority = 100\n");
            else if (twinProbed.Contains(e.Hash))
                P.Append($"filter_index = {RetexTag(e.Hash)}\nmatch_priority = 100\n");
            else
                P.Append("match_priority = 0\n");
            // A rebind hides the stock texture from the guard probes — the bound replacement answers
            // to no tag — so this section matching its hash IS the sighting: it writes the tagged
            // sibling's verdict at bind time, outside the gate (a keyed-off rebind leaves the tagged
            // stock bound, proving the same thing). The build refuses this pairing wherever the bind
            // would not prove the sibling's wardrobe option.
            foreach (var g in guardList)
                foreach (var t in g.Tags)
                    if (string.Equals(t.TexHash, e.Hash, StringComparison.OrdinalIgnoreCase))
                        P.Append($"${g.Var} = {t.Verdict}\n");
            // One gated rebind per claim, in claim order, exactly as the draw-scoped route writes one per
            // image inside its anchor section. Alternate states of one key group never open together, so
            // their rebinds coexist under one hash; where two COULD open together the build has already
            // refused any pair naming different images, and two claims naming the same one bind it twice
            // to the same effect.
            foreach (var img in e.Images)
            {
                var gate = new Gate(new KeyRef?[] { ModTerm(modKey), img.ToggleKey },
                    ShownTerms(img.ShownBy));
                gate.Open(P);
                P.Append($"this = {texRes[img.DdsFile]}\n");
                gate.Close(P);
            }
            P.Append("\n");
        }

        // One tag per stock texture a twin guard probes for and nothing else in the build tags. A hash
        // the scoped retextures already tag carries that section instead: both derive the same value
        // from the hash, so a second tag section would only restate it at every fire. A slot-tagged hash
        // never reaches here — the guard probes for the kind value its slot tag carries.
        var scopedTagged = (scoped ?? Array.Empty<ScopedRetexEntry>())
            .Select(e => e.StockHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retexTagged = entries.Select(e => e.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in mintedTwinTags)
        {
            if (scopedTagged.Contains(hash) || retexTagged.Contains(hash)) continue;
            P.Append($"[TextureOverride_TwinTag_{hash}]\nhash = {hash}\n"
                   + $"filter_index = {RetexTag(hash)}\nmatch_priority = 100\n\n");
        }

        if (scoped is { Count: > 0 })
            // one tag per stock texture: the derived filter_index the anchor probes read back.
            // match_priority marks the deliberate cross-mod duplicate (same hash, same value) so the
            // duplicate-hash warning stays quiet and ties resolve deterministically.
            foreach (var hash in scoped.Select(e => e.StockHash).Distinct(StringComparer.OrdinalIgnoreCase))
                P.Append($"[TextureOverride_RetexTag_{hash}]\nhash = {hash}\n"
                       + $"filter_index = {RetexTag(hash)}\nmatch_priority = 100\n\n");

        // One tag per picked material's own ramp, carrying the ramp KIND value, which is what says which
        // register holds a ramp at the draw. Minted here rather than beside the Replace anchors' slot tags
        // because a mod may pick a ramp on an unreplaced part and replace nothing at all.
        //
        // A hash something else already tags keeps THAT section — and the probe then reads whatever value
        // it carries. Only a slot tag of the ramp KIND carries the value this bind tests for; under any
        // other tag the bind would go out and never fire, silently. The builder holds such a pick back
        // before it gets here, so reaching one means that guard was lost, and the refusal says so rather
        // than shipping a bind that cannot work.
        foreach (var hash in ramps.Select(b => b.RampHash).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (slotTagKinds?.TryGetValue(hash, out var tagged) == true)
            {
                if (tagged == StockMapKind.Ramp) continue;
                throw new InvalidOperationException(
                    $"toon ramp {hash} is also tagged as a {tagged} map, so the ramp bind would read the "
                    + "wrong value at the draw");
            }
            if (scopedTagged.Contains(hash) || retexTagged.Contains(hash)
                || mintedTwinTags.Contains(hash, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"toon ramp {hash} is already tagged by another change in this build, so the ramp bind "
                    + "would read the wrong value at the draw");
            P.Append($"[TextureOverride_SlotTag_{hash}]\nhash = {hash}\n"
                   + $"filter_index = {FilterRamp}\nmatch_priority = 100\n\n");
        }

        // One section per distinct anchor mesh, whatever draw-scoped work lands there: each scoped texture,
        // each stock ramp pick and each stock shading change of that mesh probes and binds inside it. Every
        // save is unconditional, since a save is only a reference. Each restore runs only where this section
        // bound that register at this draw: a section that bound nothing leaves the register to whoever did,
        // because its own save may then hold another mod's resource, and putting that back after the draw
        // would leave it bound past it.
        var anchors = new List<(string Hash, string Suffix)>();
        var anchored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perAnchor = new Dictionary<string,
            List<(ScopedRetexEntry E, ScopedRetexImage I, ScopedAnchor A)>>(StringComparer.OrdinalIgnoreCase);
        var rampsAt = new Dictionary<string, List<StockRampBind>>(StringComparer.OrdinalIgnoreCase);
        var sitesAt = new Dictionary<string, List<StockDrawSite>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in scoped ?? Array.Empty<ScopedRetexEntry>())
            foreach (var img in e.Images)
            foreach (var a in img.Anchors)
            {
                if (!perAnchor.TryGetValue(a.Hash, out var list))
                    perAnchor[a.Hash] = list = new List<(ScopedRetexEntry, ScopedRetexImage, ScopedAnchor)>();
                if (anchored.Add(a.Hash)) anchors.Add((a.Hash, a.Suffix));
                list.Add((e, img, a));
            }
        foreach (var b in ramps)
        {
            if (!rampsAt.TryGetValue(b.IbHash, out var list))
                rampsAt[b.IbHash] = list = new List<StockRampBind>();
            if (anchored.Add(b.IbHash)) anchors.Add((b.IbHash, b.Name));
            list.Add(b);
        }
        foreach (var site in sites)
        {
            if (!sitesAt.TryGetValue(site.IbHash, out var list))
                sitesAt[site.IbHash] = list = new List<StockDrawSite>();
            if (anchored.Add(site.IbHash)) anchors.Add((site.IbHash, site.Id));
            list.Add(site);
        }
        var usedSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ibHash, first) in anchors)
        {
            var body = new List<string>();
            bool hasScoped = perAnchor.TryGetValue(ibHash, out var scopedHere);
            bool hasRamps = rampsAt.TryGetValue(ibHash, out var rampsHere);
            var sitesHere = sitesAt.GetValueOrDefault(ibHash) ?? new List<StockDrawSite>();
            var groupsHere = sitesHere.SelectMany(site => groupsBySite[site.Id]).ToList();
            var patchedHere = groupsHere.Where(HasPatchShader).ToList();
            // Every register a bind in this section can touch, with the save it is put back from. The families
            // overlap, so a register takes the first family's save and keeps it: one save and one restore
            // per register, whatever wanted it.
            var saves = new SortedDictionary<int, string>();
            if (hasScoped)
                foreach (int s in scopedHere!.SelectMany(x => x.E.Registers ?? ProbeSlots))
                    saves.TryAdd(s, $"Resource_RtxSave{s}");
            if (hasRamps)
                foreach (int s in Slots.Ramp) saves.TryAdd(s, $"Resource_SrSave{s}");
            foreach (var group in groupsHere)
                foreach (var texture in PatchTextures(group))
                    saves.TryAdd(texture.Slot, $"Resource_MaterialTextureSave_{group.Gid}_{texture.Slot}");
            // The bound flags and the pixel-program answers are declared at the section's top level, where
            // the restores after the draw can read them.
            foreach (int s in saves.Keys) body.Add($"local ${BoundVar(s)} = 0");
            foreach (var group in patchedHere) body.Add($"local ${BoundCbVar(group.Gid)} = 0");
            foreach (var group in groupsHere) body.Add($"local $zz_material_ps_{group.Gid}");
            if ((rampsHere ?? new List<StockRampBind>()).Any(b => b.Material is not null)
                || sitesHere.Any(site => site.Material is not null))
            {
                body.Add($"local ${VarMaterialProbe}");
                body.Add($"local ${VarMaterialSeen}");
            }
            // EVERY save first, ahead of every probe and every bind in this section. A save taken after a
            // bind captures the mod's own resource, and its restore would then leave that resource bound
            // past the draw instead of putting the game's back — which is what a section carrying both a
            // scoped retexture and a ramp bind did on the registers the two ranges share.
            foreach (var (s, save) in saves) body.Add($"{save} = ref ps-t{s}");
            foreach (var group in patchedHere)
                body.Add($"Resource_MaterialSource_{group.Gid} = ref ps-cb{group.ConstantBufferSlot}");
            if (hasScoped)
            {
                // ONE probe per stock texture, ahead of every image that binds through it: a bind replaces
                // the tagged resource in the slot, so a probe run after one finds no tag and its own image
                // would never bind. The images then differ only in their gate.
                foreach (var group in scopedHere!.GroupBy(x => x.E.StockHash,
                             StringComparer.OrdinalIgnoreCase))
                {
                    int tag = RetexTag(group.First().E.StockHash);
                    var registers = group.SelectMany(x => x.E.Registers ?? ProbeSlots)
                        .Distinct().OrderBy(x => x).ToList();
                    body.Add($"${VarRetexSlot} = -1");
                    foreach (int s in registers)
                    {
                        body.Add($"${VarRetexProbe} = ps-t{s}");
                        body.Add($"if ${VarRetexProbe} == {tag}");
                        body.Add($"${VarRetexSlot} = {s}");
                        body.Add("endif");
                    }
                    foreach (var (_, img, a) in group)
                    {
                        var gate = new Gate(new KeyRef?[] { ModTerm(modKey), img.ToggleKey },
                            With(LatchTerms(a.Latch), ShownTerm(img.ShownBy)));
                        body.AddRange(gate.Wrap(registers.SelectMany(s => new[]
                        {
                            $"if ${VarRetexSlot} == {s}", $"ps-t{s} = {texRes[img.DdsFile]}",
                            $"${BoundVar(s)} = 1", "endif",
                        })));
                    }
                }
            }
            // The work on one material's own draw: the section fires at every draw of the mesh, and the
            // draw's first index and index count say which material it is.
            var shapes = (rampsHere ?? new List<StockRampBind>()).Select(b => b.Shape)
                .Concat(sitesHere.Select(site => site.Shape)).Distinct()
                .OrderBy(shape => shape.First).ThenBy(shape => shape.Count).ToList();
            foreach (var shape in shapes)
            {
                body.Add($"if first_index == {shape.First}");
                body.Add($"if index_count == {shape.Count}");
                var rampsAtShape = (rampsHere ?? new List<StockRampBind>()).Where(b => b.Shape == shape).ToList();
                var sitesAtShape = sitesHere.Where(site => site.Shape == shape).ToList();
                // One material draws at this range, so every change here tells it apart the same way: where
                // another outfit wears the mesh, by the material's own texture being bound.
                var probes = rampsAtShape.Select(b => b.Material)
                    .Concat(sitesAtShape.Select(site => site.Material)).Distinct().ToList();
                if (probes.Count > 1)
                    throw new InvalidOperationException(
                        $"two changes at one draw of {ibHash} identify its material by different textures");
                if (probes[0] is { } probe)
                {
                    body.Add($"${VarMaterialSeen} = 0");
                    foreach (int s in ProbeSlots)
                    {
                        body.Add($"${VarMaterialProbe} = ps-t{s}");
                        body.Add($"if ${VarMaterialProbe} == {probe.TagValue}");
                        body.Add($"${VarMaterialSeen} = 1");
                        body.Add("endif");
                    }
                    body.Add($"if ${VarMaterialSeen} == 1");
                }
                foreach (var b in rampsAtShape) body.AddRange(StockRampBody(b, modKey, texRes, guards));
                foreach (var site in sitesAtShape)
                    body.AddRange(StockSiteBody(site, groupsBySite[site.Id], modKey, guards));
                if (probes[0] is not null) body.Add("endif");
                body.Add("endif");
                body.Add("endif");
            }
            // One restore per register, and only where this section bound it at this draw. Post commands
            // run in source order and the last one on a register wins, so a register two families bind is
            // named once, from the save the first of them took.
            foreach (var (s, save) in saves)
            {
                body.Add($"if ${BoundVar(s)} == 1");
                body.Add($"post ps-t{s} = {save}");
                body.Add("endif");
            }
            foreach (var group in patchedHere)
            {
                body.Add($"if ${BoundCbVar(group.Gid)} == 1");
                body.Add($"post ps-cb{group.ConstantBufferSlot} = Resource_MaterialSource_{group.Gid}");
                body.Add("endif");
            }

            // A mesh this build already captures owns its ONE section: the block runs there instead
            // of minting a second override on the same hash, which 3DMigoto would drop at parse time.
            if (units.ByHash.TryGetValue(ibHash, out var owner))
            {
                owner.ScopeLines.AddRange(body);
                continue;
            }
            // A rigid replacement's section owns its hashes the same way, and folds identically: a
            // Replace and a scoped retexture on one part are a supported pair on either route.
            if (rigidOwner is not null && rigidOwner.TryGetValue(ibHash, out var rigid))
            {
                if (!rigid.ScopeLines.TryGetValue(ibHash, out var into))
                    rigid.ScopeLines[ibHash] = into = new List<string>();
                into.AddRange(body);
                continue;
            }
            // A hidden mesh's section owns its hash the same way, and folds identically: the skip and the
            // repaint are one section's business. While a hiding position stands the draw is skipped and
            // nothing is left for the bind to repaint, so the two never contradict each other.
            if (hideScope is not null && hideScope.TryGetValue(ibHash, out var hidden))
            {
                hidden.AddRange(body);
                continue;
            }
            // duplicate-named sections drop silently at parse time, so a suffix two anchors share
            // (one character's two outfits, same part token) gets disambiguated here
            string suffix = first;
            while (!usedSuffixes.Add(suffix)) suffix += "_";
            OpenTextureOverride(P, $"RetexScope_{suffix}", ibHash);
            if (sightings.ByHash.TryGetValue(ibHash, out var seen))
                foreach (var line in seen) P.Append(line).Append('\n');
            // A stock change under a twin guard acts on its own mesh's verdict. The probe that writes the
            // verdict runs here, at every draw of the mesh: the texture it looks for is bound at one material's
            // draw only, and the verdict carries to the others. A section this body folds into above already
            // runs that probe at its own top.
            if (guards.TryGetValue(ibHash, out var stockGuard)
                && ((rampsHere ?? new List<StockRampBind>()).Any(b => b.TwinVerdict is not null)
                    || sitesHere.Any(site => site.TwinVerdict is not null)))
                AppendTwinProbe(P, stockGuard);
            foreach (var line in body) P.Append(line).Append('\n');
            P.Append("\n");
        }
        return P.ToString();
    }

    /// <summary>One draw-scoped ramp bind, inside its material's draw-range test: find which register holds a
    /// ramp at this draw and put the picked ramp there. The section's saves and restores are the caller's —
    /// they belong to the whole section, not to this block — and this block raises the bound flag of the
    /// register it wrote, which is what lets the restore run.
    ///
    /// <para>The draw range answers WHICH MATERIAL is drawing; the ramp tag answers WHICH REGISTER holds its
    /// ramp, which the draw range cannot, since registers move between shader variants and scenes. A pass
    /// binding no ramp, such as a depth or shadow pass, finds no register and leaves every one as it found
    /// it.</para></summary>
    IEnumerable<string> StockRampBody(StockRampBind b, string? modKey,
        IReadOnlyDictionary<string, string> texRes, IReadOnlyDictionary<string, TwinGuard> guards)
    {
        // the verdict is written by the probe at the top of the mesh's section, at every draw of the mesh
        var guard = StockTwinGuard(b.IbHash, b.TwinVerdict, b.Name, guards);
        if (guard is not null) yield return $"if ${guard.Var} == {b.TwinVerdict}";
        yield return $"${VarRampSlot} = -1";
        foreach (int s in Slots.Ramp)
        {
            yield return $"${VarStockRampProbe} = ps-t{s}";
            yield return $"if ${VarStockRampProbe} == {FilterRamp}";
            yield return $"${VarRampSlot} = {s}";
            yield return "endif";
        }
        var gate = new Gate(new KeyRef?[] { ModTerm(modKey), b.ToggleKey },
            With(LatchTerms(b.Latch), ShownTerm(b.ShownBy)));
        foreach (var line in gate.Wrap(Slots.Ramp.SelectMany(s => new[]
                 {
                     $"if ${VarRampSlot} == {s}", $"ps-t{s} = {texRes[b.DdsFile]}", $"${BoundVar(s)} = 1", "endif",
                 })))
            yield return line;
        if (guard is not null) yield return "endif";
    }

    /// <summary>The material patches of one stock draw site, inside its draw-range test and exactly as the
    /// replacement route wraps a donor draw, except that the draw is the game's own: each group's gate is
    /// its exact pixel program being bound; a group that omits the draw skips it; every other group runs its
    /// pass over the live constant buffer, binds the patched copy and its neutral textures,
    /// and raises the bound flags the section's restores read after the draw.</summary>
    IEnumerable<string> StockSiteBody(StockDrawSite site, IReadOnlyList<MaterialPatchGroup> groups,
        string? modKey, IReadOnlyDictionary<string, TwinGuard> guards)
    {
        var guard = StockTwinGuard(site.IbHash, site.TwinVerdict, site.Id, guards);
        if (guard is not null) yield return $"if ${guard.Var} == {site.TwinVerdict}";
        var gate = new Gate(new KeyRef?[] { ModTerm(modKey), site.ToggleKey },
            With(LatchTerms(site.Latch), ShownTerm(site.ShownBy)));
        foreach (var group in groups) yield return $"$zz_material_ps_{group.Gid} = ps";
        foreach (var group in groups)
        {
            yield return $"if $zz_material_ps_{group.Gid} == {group.FilterIndex}";
            var lines = new List<string>();
            if (group.Patches.Any(patch => patch.SkipDraw)) lines.Add("handling = skip");
            else
            {
                if (HasPatchShader(group))
                {
                    lines.AddRange(PatchLines(group));
                    lines.Add($"${BoundCbVar(group.Gid)} = 1");
                }
                foreach (var texture in PatchTextures(group))
                {
                    lines.Add($"ps-t{texture.Slot} = Resource_MaterialTexture_{group.Gid}_{texture.Slot}");
                    lines.Add($"${BoundVar(texture.Slot)} = 1");
                }
            }
            foreach (var line in gate.Wrap(lines)) yield return line;
            yield return "endif";
        }
        if (guard is not null) yield return "endif";
    }

    /// <summary>The twin guard a stock draw's work opens, or null where its mesh key has none. A work item
    /// on a guarded key must name the verdict of its own mesh, and a verdict must be one the guard admits:
    /// either missing would act on the other mesh's draws.</summary>
    static TwinGuard? StockTwinGuard(string hash, int? verdict, string name,
        IReadOnlyDictionary<string, TwinGuard> guards)
    {
        if (!guards.TryGetValue(hash, out var guard))
            return verdict is null ? null
                : throw new InvalidOperationException(
                    $"'{name}' names twin verdict {verdict} on {hash}, which no guard holds");
        if (verdict is not { } own || !guard.OwnVerdicts.Contains(own))
            throw new InvalidOperationException(
                $"'{name}' acts on {hash}, which another mesh also draws on, without its own twin verdict");
        return guard;
    }

    static bool WritesConstants(MaterialPatchEmission patch) => patch.Writes is { Count: > 0 };

    static bool HasPatchShader(MaterialPatchGroup group) =>
        group.Patches.Any(WritesConstants);

    static IEnumerable<MaterialEffectTexture> PatchTextures(MaterialPatchGroup group) =>
        group.Patches.SelectMany(patch => patch.TextureOverrides ?? Array.Empty<MaterialEffectTexture>())
            .Distinct();

    /// <summary>The <c>[Constants]</c> declaration of every distinct key — the ONE place a key variable is
    /// declared, so both build routes start a key the same way. A key is declared at the position its cycle
    /// launches in. A toggle is PER-SESSION unless its cycle opts out: the declared value is where a
    /// session starts, and a <c>persist</c> key's saved position, restored from the runtime's user config
    /// after this section's declarations, then overrides it.</summary>
    static string KeyDeclarations(IReadOnlyList<string> keys, string? modKey,
        IReadOnlyCollection<string>? startingOff, IReadOnlyList<KeyCycle>? cycles,
        bool persistModKey = false)
    {
        var P = new StringBuilder();
        foreach (var k in keys)
        {
            var cycle = CycleFor(k, modKey, startingOff, cycles, persistModKey);
            P.Append($"global {(cycle.Persist ? "persist " : "")}${ModKeys.VariableFor(k)} = "
                   + $"{cycle.StartState}\n");
        }
        return P.ToString();
    }

    /// <summary>How one key steps. EVERY key is ordinal: one variable stepping 0, 1, … and wrapping, with
    /// position 0 the one a mod's content draws in. The mod's OWN key is the whole-mod switch rather than a
    /// group, so it keeps exactly two positions — on at 0, off at 1 — even where a group is bound to the
    /// same key; a change sharing the mod's key then reads the same variable at the same position as the mod
    /// gate does, rather than one asking for 1 while the other asks for 0. Every other key belongs to a key
    /// group and cycles that group's positions; a key no cycle names is a two-state group, launching at 0
    /// unless it starts off, which is where a released two-state project lands.
    ///
    /// <para>The mod's own key persists when the mod opts in OR when a group sharing that key does: the two
    /// tiers read one variable, so either owner's choice keeps it.</para></summary>
    static KeyCycle CycleFor(string key, string? modKey, IReadOnlyCollection<string>? startingOff,
        IReadOnlyList<KeyCycle>? cycles, bool persistModKey = false)
    {
        bool off = (startingOff ?? Array.Empty<string>()).Any(k => ModKeys.SameKey(k, key));
        var named = cycles?.FirstOrDefault(c => ModKeys.SameKey(c.Key, key));
        if (ModKeys.SameKey(key, modKey))
            return new KeyCycle(key, 2, off ? 1 : 0, persistModKey || named?.Persist == true);
        if (named is not null) return named;
        return new KeyCycle(key, 2, off ? 1 : 0);
    }

    /// <summary>The mod's own key as a gate term. On is position 0, like every other key's content position,
    /// so a change bound to the mod's own key contributes a term identical to this one and the two collapse
    /// into a single <c>if</c> instead of nesting a contradiction.</summary>
    static KeyRef? ModTerm(string? modKey) =>
        modKey is null ? (KeyRef?)null : new KeyRef(modKey, 0);

    /// <summary>The key sections — one pair per distinct key, whatever the tier (two changes on one key
    /// share one variable). Every key STEPS its variable to the next position and wraps at the end of its
    /// cycle, the mod's own key included: its cycle is two positions long, so the step is the flip it has
    /// always been, written the one way every key is written. Start-agnostic. Emits NOTHING when no key is
    /// bound.
    ///
    /// <para>The step lives in a <c>[CommandList…]</c> the <c>[Key…]</c> section <c>run</c>s: 3DMigoto
    /// parses a <c>[Key…]</c> section as a KeyOverride, not a command list, so a variable assignment
    /// written there is dropped at parse time and the press does nothing.</para>
    ///
    /// <para>Any press can change any hider flag — the state it left is one another part's content was
    /// suppressed by — so every key's command list ends by re-running the shared recompute.</para>
    ///
    /// <para>A key with no modifiers is bound <c>no_modifiers</c>: a bare <c>key = F6</c> also fires on
    /// CTRL+F6, which would fire two toggles at once beside a distinct CTRL F6 binding.</para>
    ///
    /// <para>A state shortcut sets its group's variable to that state's position. A shortcut that is also a
    /// stepping key writes its assignments into that key's command list after the step, so one press does
    /// both. Any other shortcut key gets a section pair of its own. Either way the recompute runs once,
    /// after every assignment the press makes.</para></summary>
    static string KeysIni(string? modKey, IEnumerable<string?> changeKeys,
        IReadOnlyCollection<string>? startingOff = null, IReadOnlyList<KeyCycle>? cycles = null,
        bool recomputeHidden = false)
    {
        var keys = ModKeys.Distinct(new[] { modKey }.Concat(changeKeys));
        if (keys.Count == 0) return "";
        var jumps = Jumps(keys, cycles);
        var P = new StringBuilder();
        void Section(string k, bool steps)
        {
            string v = ModKeys.VariableFor(k);
            // normalized keys are modifier tokens then ONE key token, so a single token means none named
            string binding = k.Contains(' ') ? k : $"no_modifiers {k}";
            P.Append($"[Key_{v}]\nkey = {binding}\nrun = CommandListKey_{v}\n\n");
            P.Append($"[CommandListKey_{v}]\n");
            if (steps)
            {
                int count = CycleFor(k, modKey, startingOff, cycles).StateCount;
                P.Append($"${v} = ${v} + 1\nif ${v} == {count}\n${v} = 0\nendif\n");
            }
            if (jumps.TryGetValue(k, out var sets))
                foreach (var (variable, state) in sets) P.Append($"${variable} = {state}\n");
            if (recomputeHidden) P.Append($"run = {SectionRecomputeHidden}\n");
            P.Append('\n');
        }
        foreach (var k in keys) Section(k, steps: true);
        foreach (var k in jumps.Keys.Where(k => !keys.Contains(k, StringComparer.Ordinal)))
            Section(k, steps: false);
        return P.ToString();
    }

    /// <summary>What each shortcut key sets: the variable of every group it jumps and the position it
    /// jumps that group to, keyed by the shortcut in first-seen order. Only a group whose key this build
    /// declares is jumped. A group that switches nothing in this build has no variable to set.</summary>
    static OrderedDictionary<string, List<(string Variable, int State)>> Jumps(IReadOnlyList<string> declared,
        IReadOnlyList<KeyCycle>? cycles)
    {
        var jumps = new OrderedDictionary<string, List<(string Variable, int State)>>(StringComparer.Ordinal);
        foreach (var cycle in cycles ?? Array.Empty<KeyCycle>())
        {
            if (ModKeys.Normalize(cycle.Key) is not { } key || !declared.Contains(key, StringComparer.Ordinal))
                continue;
            foreach (var shortcut in cycle.Shortcuts ?? Array.Empty<KeyShortcut>())
            {
                if (ModKeys.Normalize(shortcut.Key) is not { } jump) continue;
                if (!jumps.TryGetValue(jump, out var sets)) jumps[jump] = sets = new();
                sets.Add((ModKeys.VariableFor(key), shortcut.State));
            }
        }
        return jumps;
    }

    /// <summary>The one command list that recomputes every hider flag. Named once so the <c>[Constants]</c>
    /// run and every key's run name the same section.</summary>
    const string SectionRecomputeHidden = "CommandListRecomputeHidden";

    /// <summary>The variable carrying whether one part is currently suppressed by ANOTHER group's state.
    /// Derived from the part's own emission name, so two parts never share a flag.</summary>
    static string HiddenVar(string name) => "zz_hid_" + name;

    /// <summary>The variable carrying whether one change's own group currently stands in a position that
    /// answers with it. Derived from the change's emission name, so two never share a flag.</summary>
    static string ShownVar(string name) => "zz_shw_" + name;

    /// <summary>The shared recompute: each flag is cleared, then raised by every state that hides its part.
    /// Run from <c>[Constants]</c> so a session starts with the flags its launch states imply, and from
    /// every key's command list because any press can change any of them.</summary>
    static string RecomputeHiddenIni(IReadOnlyList<HiddenFlag> flags,
        IReadOnlyList<ShownFlag>? shown = null)
    {
        var content = shown ?? Array.Empty<ShownFlag>();
        if (flags.Count == 0 && content.Count == 0) return "";
        var P = new StringBuilder($"[{SectionRecomputeHidden}]\n");
        foreach (var flag in flags) Recompute(HiddenVar(flag.Name), flag.WhenAny);
        // after the hider flags, so a build that mints none is byte-identical to the emission that
        // predates content flags
        foreach (var flag in content) Recompute(ShownVar(flag.Name), flag.WhenAny);
        return P.Append('\n').ToString();

        void Recompute(string v, IReadOnlyList<KeyRef> when)
        {
            P.Append($"${v} = 0\n");
            foreach (var term in when)
                if (ModKeys.NormalizeRef(term) is { } n)
                    P.Append($"if ${ModKeys.VariableFor(n.Key)} == {n.State}\n${v} = 1\nendif\n");
        }
    }

    /// <summary>The mod-wide variables + per-frame flag reset, and one <c>[TextureOverride]</c> slot tag
    /// per stock map. 3DMigoto namespaces named variables per ini file, so two of these mods never collide
    /// on the draw's probe variables. The tags carry no command list — only a <c>filter_index</c> the
    /// draw's probe reads back through the slot operands.
    ///
    /// <para>A presence latch adds its two variables, a <c>[Present]</c> commit (gate ← last frame's
    /// sighting, sighting cleared), and a witness section per witness ib no other section already
    /// claims (see <see cref="Sightings"/>).</para></summary>
    string FlagsIni(IReadOnlyList<string>? perFrameFlags, IReadOnlyList<StockMapTag> slotTags,
        string? modKey, IEnumerable<string?>? changeKeys,
        IReadOnlyList<WitnessLatch>? latches, Sightings sightings, bool scopedRetex = false,
        IReadOnlySet<string>? scopedHashes = null, IReadOnlyCollection<string>? keysStartingOff = null,
        IReadOnlyList<string>? twinVars = null, bool twinScratch = false,
        IReadOnlySet<string>? retexturedHashes = null, IReadOnlyList<string>? stickyFlags = null,
        bool rampTexed = false, bool stockRamped = false,
        IReadOnlyList<KeyCycle>? keyCycles = null, IReadOnlyList<HiddenFlag>? hiddenFlags = null,
        IReadOnlyList<ShownFlag>? shownFlags = null, bool blendTexed = false,
        IReadOnlyList<StockPropertyTag>? propertyTags = null, bool persistModKey = false,
        IReadOnlyList<string>? constantsRuns = null, IReadOnlyList<string>? presentRuns = null)
    {
        var hidden = hiddenFlags ?? Array.Empty<HiddenFlag>();
        var shown = shownFlags ?? Array.Empty<ShownFlag>();
        var keys = ModKeys.Distinct(new[] { modKey }.Concat(changeKeys ?? Array.Empty<string?>()));
        var P = new StringBuilder($"[Constants]\nglobal ${VarProbe} = 0\nglobal ${VarAlbedoSlot} = 0\n"
            + $"global ${VarNormalSlot} = 0\nglobal ${VarRmoSlot} = 0\n");
        // declared only where a ramp ships, so a build without one is byte-identical to the emission that
        // predates ramps
        if (rampTexed) P.Append($"global ${VarRampSlot} = 0\n");
        if (blendTexed) P.Append($"global ${VarBlendSlot} = 0\n");
        foreach (var property in (propertyTags ?? Array.Empty<StockPropertyTag>())
                     .Select(t => t.ShaderProperty).Distinct(StringComparer.Ordinal)
                     .OrderBy(p => p, StringComparer.Ordinal))
            P.Append($"global ${PropertyVar(property)} = 0\n");
        // declared here and written only by the guard probes: the [Present] resets below leave them
        // alone, which is what carries a verdict across the passes that bind no identifying texture
        foreach (var v in twinVars ?? Array.Empty<string>()) P.Append($"global ${v} = 0\n");
        // the multi-verdict guards' scratch, rewritten at every guard it opens rather than carried
        if (twinScratch) P.Append($"global ${VarTwinOk} = 0\n");
        foreach (var f in perFrameFlags ?? Array.Empty<string>()) P.Append($"global ${f} = 0\n");
        // declared beside the per-frame flags and left out of the [Present] reset below. Two kinds: a flag
        // recording that a capture has happened at all, which is a per-SESSION fact, and a stream
        // selector, which every draw that reads it writes first
        foreach (var f in stickyFlags ?? Array.Empty<string>()) P.Append($"global ${f} = 0\n");
        if (scopedRetex) P.Append($"global ${VarRetexProbe} = 0\nglobal ${VarRetexSlot} = 0\n");
        // declared only where a ramp is picked on an unreplaced part
        if (stockRamped) P.Append($"global ${VarStockRampProbe} = 0\n");
        foreach (var l in latches ?? Array.Empty<WitnessLatch>())
            P.Append($"global ${GateVar(l.Name)} = 0\nglobal ${SeenVar(l.Name)} = 0\n");
        // one per part another group's state takes off screen, declared beside the keys that raise them and
        // recomputed right below, so a session opens with the answer its launch states imply
        foreach (var flag in hidden) P.Append($"global ${HiddenVar(flag.Name)} = 0\n");
        // one per change answering more than one position, declared beside the hider flags and recomputed
        // in the same place: both answer to the positions the keys currently stand in
        foreach (var flag in shown) P.Append($"global ${ShownVar(flag.Name)} = 0\n");
        P.Append(KeyDeclarations(keys, modKey, keysStartingOff, keyCycles, persistModKey));
        if (hidden.Count + shown.Count > 0)
        {
            P.Append($"run = {SectionRecomputeHidden}\n");
            // a persist key's saved position arrives from the runtime's user config, which is loaded
            // last — after this run — so the flags it implies are recomputed once more post-restore
            if (keys.Any(k => CycleFor(k, modKey, keysStartingOff, keyCycles, persistModKey).Persist))
                P.Append($"post run = {SectionRecomputeHidden}\n");
        }
        // run once when the mod loads (and again on a reload), after the declarations above
        foreach (var line in constantsRuns ?? Array.Empty<string>()) P.Append(line).Append('\n');
        P.Append("\n");
        P.Append(RecomputeHiddenIni(hidden, shown));
        if (perFrameFlags is { Count: > 0 } || latches is { Count: > 0 } || presentRuns is { Count: > 0 })
        {
            P.Append("[Present]\n");
            foreach (var f in perFrameFlags ?? Array.Empty<string>()) P.Append($"${f} = 0\n");
            foreach (var line in presentRuns ?? Array.Empty<string>()) P.Append(line).Append('\n');
            foreach (var l in latches ?? Array.Empty<WitnessLatch>())
                P.Append($"${GateVar(l.Name)} = ${SeenVar(l.Name)}\n${SeenVar(l.Name)} = 0\n");
            P.Append("\n");
        }
        P.Append(KeysIni(modKey, changeKeys ?? Array.Empty<string?>(), keysStartingOff, keyCycles,
            hidden.Count + shown.Count > 0));
        foreach (var t in slotTags)
        {
            // A scoped-retextured stock hash already carries its RetexTag section; a second section
            // with a second filter_index on the same hash would leave the probe's answer to the
            // priority sort. The draw probe accepts the RetexTag value for the kind instead.
            if (scopedHashes?.Contains(t.Hash) == true) continue;
            // A plain-retextured stock hash carries its kind value on the retexture's own section —
            // a second section here would trip the runtime's mod-conflict warning.
            if (retexturedHashes?.Contains(t.Hash) == true) continue;
            P.Append($"[TextureOverride_SlotTag_{t.Hash}]\nhash = {t.Hash}\n"
                   + $"filter_index = {KindFilter(t.Kind)}\nmatch_priority = 100\n\n");
        }
        var fixedHashes = slotTags.Select(t => t.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in propertyTags ?? Array.Empty<StockPropertyTag>())
        {
            if (fixedHashes.Contains(t.Hash) || scopedHashes?.Contains(t.Hash) == true
                || retexturedHashes?.Contains(t.Hash) == true) continue;
            P.Append($"[TextureOverride_PropertyTag_{t.Hash}]\nhash = {t.Hash}\n"
                   + $"filter_index = {RetexTag(t.Hash)}\nmatch_priority = 100\n\n");
            fixedHashes.Add(t.Hash);
        }
        P.Append(WitnessIni(sightings));
        return P.ToString();
    }

    // ---- json (CRLF, one-space indent — the emitted-text contract shape) ---------------------------

    static string UnionJson(int ub, uint[] order, List<(string Part, int N, int Nb, int Rows)> partMeta)
    {
        var sb = new StringBuilder();
        sb.Append("{\r\n");
        sb.Append($" \"unionBones\": {ub},\r\n");
        sb.Append(" \"order\": [\r\n");
        for (int i = 0; i < order.Length; i++)
            sb.Append($"  \"{order[i]}\"").Append(i + 1 < order.Length ? ",\r\n" : "\r\n");
        sb.Append(" ],\r\n");
        sb.Append(" \"parts\": [\r\n");
        for (int i = 0; i < partMeta.Count; i++)
        {
            var (part, n, nb, _) = partMeta[i];
            sb.Append("  {\r\n");
            sb.Append($"   \"part\": \"{part}\",\r\n");
            sb.Append($"   \"verts\": {n},\r\n");
            sb.Append($"   \"bones\": {nb}\r\n");
            sb.Append("  }").Append(i + 1 < partMeta.Count ? ",\r\n" : "\r\n");
        }
        sb.Append(" ]\r\n");
        sb.Append("}");
        return sb.ToString();
    }

    static string CombinedMetaJson(int verts, int vb1Stride, List<PoolMath.Submesh> submeshes)
    {
        var sb = new StringBuilder();
        sb.Append("{\r\n");
        sb.Append($" \"verts\": {verts},\r\n");
        sb.Append(" \"indexFormat\": \"R16_UINT\",\r\n");
        sb.Append($" \"vb1_stride\": {vb1Stride},\r\n");
        sb.Append(" \"submeshes\": [\r\n");
        for (int i = 0; i < submeshes.Count; i++)
        {
            var s = submeshes[i];
            sb.Append("  {\r\n");
            sb.Append($"   \"firstByte\": {s.FirstByte},\r\n");
            sb.Append($"   \"indexCount\": {s.IndexCount},\r\n");
            sb.Append($"   \"baseVertex\": {s.BaseVertex}\r\n");
            sb.Append("  }").Append(i + 1 < submeshes.Count ? ",\r\n" : "\r\n");
        }
        sb.Append(" ]\r\n");
        sb.Append("}");
        return sb.ToString();
    }

    // ---- operator conditioning ---------------------------------------------------------------------

    /// <summary>One solved operator, or the failure its solve raised — held so the failure surfaces where a
    /// serial build would have raised it, not wherever the scheduler happened to run the job.</summary>
    readonly record struct OperatorSolve(OperatorArt? Art, ExceptionDispatchInfo? Error);

    /// <summary>Solve every distinct (name, dump dir) operator ahead of the emission that consumes them.
    /// <see cref="BuildOperator"/> is pure and the build's dominant cost, so the set is solved in parallel;
    /// everything order-dependent (diagnostics, writes, failures) stays in the emission's own sequence. A
    /// pair the emission never reaches is solved and discarded, its failure never raised. Parallelism is
    /// capped by <see cref="CpuLimit"/>, and by the machine's logical processor count without one. This is
    /// the ONLY fan-out on the route — <see cref="BuildOperator"/> and everything under it run serially — so
    /// that cap is the whole width the solve takes.</summary>
    Dictionary<(string Name, string Dir), OperatorSolve> SolveOperators(PoolBuildRequest req,
        Func<string, StreamsLoad> load, Func<string, PoolMath.UnionInput> unionInput,
        Func<string, Matrix4x4?> conversion,
        IReadOnlyDictionary<(string Name, string Dir), HashSet<int>>? retainedRows = null,
        bool classificationOnly = false)
    {
        // Pool parts and tiers tie a bone no slim selection holds; a wardrobe member never ties (its rows are
        // not ridden by the donor's geometry, see the group emission), so it keeps the dense width.
        var jobs = new List<(string Name, string Dir, string? OpKey, bool TieSlim)>();
        var seen = new HashSet<(string, string)>();
        foreach (var pipe in req.Pipelines)
        {
            foreach (var p in pipe.Parts)
                if (seen.Add((p.Name, p.DumpDir))) jobs.Add((p.Name, p.DumpDir, p.OpKey, true));
            foreach (var t in pipe.Tiers ?? Array.Empty<PoolTier>())
                if (seen.Add((t.Name, t.DumpDir))) jobs.Add((t.Name, t.DumpDir, t.OpKey, true));
        }
        foreach (var pipe in req.Pipelines)
            foreach (var m in GroupMeshes(pipe))
                if (seen.Add((m.Name, m.DumpDir))) jobs.Add((m.Name, m.DumpDir, m.OpKey, false));
        if (retainedRows is not null)
            jobs = jobs.Where(j => retainedRows.TryGetValue((j.Name, j.Dir), out var rows)
                    && rows.Count > 0).ToList();

        var solved = new ConcurrentDictionary<(string, string), OperatorSolve>();
        Parallel.ForEach(jobs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, CpuLimit ?? Environment.ProcessorCount) },
            job =>
            {
                OperatorSolve result;
                try
                {
                    var rows = retainedRows is not null
                        ? retainedRows[(job.Name, job.Dir)].OrderBy(i => i).ToArray()
                        : Enumerable.Range(0, load(job.Dir).Nb).ToArray();
                    string? key = OperatorCacheKey(job.OpKey, job.Name, conversion(job.Dir), rows, job.TieSlim);
                    var art = key is null ? null : ReadCachedOperator(OperatorCachePath(key), key);
                    if (art is null)
                    {
                        art = BuildOperator(load(job.Dir), unionInput(job.Dir).Hashes, job.Name, rows,
                            classificationOnly, job.TieSlim, BonePathsOf(req));
                        // Classification artifacts deliberately stop before the shipped operator exists, so
                        // they cannot stand under the retained solve's unchanged persistent-cache identity.
                        if (!classificationOnly && key is not null)
                            WriteCachedOperator(OperatorCachePath(key), key, art);
                    }
                    result = new OperatorSolve(art, null);
                }
                catch (Exception ex) { result = new OperatorSolve(null, ExceptionDispatchInfo.Capture(ex)); }
                solved[(job.Name, job.Dir)] = result;
            });
        return new Dictionary<(string, string), OperatorSolve>(solved);
    }

    /// <summary>A part/tier's recovery operator with its conditioning verdict. <see cref="Sel"/> non-null
    /// = the SLIM operator shipped, in the RAGGED layout: <see cref="Off"/> holds (base, width) per bone,
    /// the bone's anchor vertices are <c>Sel[base .. base+width)</c> and its four <see cref="Cpinv"/> rows
    /// of <c>width</c> coefficients start at float index <c>4*base</c>. Null = the DENSE all-vertex operator
    /// shipped, its rows spanning all <see cref="N"/> vertices. Dense is always computed — it is the
    /// conditioning authority — but ships only when the slim layout would not be smaller.
    /// <see cref="Weak"/> marks the bones whose rows a tie replaced. The dense residual decides it first: a
    /// deterministic synthetic-palette measurement that depends only on bind positions and weights, never on
    /// pose. The slim rows carry a separate gate; a pool part's or tier's bone that cannot hold it takes a tie
    /// to a sound bone that can, and joins <see cref="Weak"/> (a classification-only art never carries these);
    /// a bone with no such bone, or a wardrobe member's, widens to every vertex on its own, which is why no
    /// bone's conditioning can decline slimming for the part.
    /// A weak bone's rows AND its Sel segment are replaced by its <see cref="TieFullRows"/> bone's, so its geometry
    /// rides that bone rigidly instead of taking a min-norm estimate — valid without space conversion,
    /// since every palette row maps the mesh's bind space to the posed space. <see cref="TieFullRows"/> is
    /// indexed by the compact output row like <see cref="Weak"/> and <see cref="Hashes"/>, but each
    /// nonnegative VALUE is a FULL local source row (the space recorded by <see cref="SourceRows"/>), not
    /// a compact index. -1 means the mesh has no sound bone at all: the bone keeps its own rows, and tier
    /// scatter sentinels it to its lod0 row.</summary>
    internal sealed record OperatorArt(float[] Cpinv, bool[] Weak, int[] TieFullRows, uint[] Hashes,
        int[] SourceRows, IReadOnlyList<string> Diagnostics, uint[]? Sel, uint[]? Off, int N);

    /// <summary>Max acceptable |recovered − true| row error in the DENSE synthetic residual (an
    /// absolute bound against the O(1) probe palette).</summary>
    const double OperatorErrGate = 0.01;

    /// <summary>Max acceptable slim LEFT-INVERSE DEFECT — a different quantity than
    /// <see cref="OperatorErrGate"/>: the defect is relative (recovery error scales as
    /// defect × the palette's row magnitudes), so this bounds error per unit of palette, not an absolute
    /// residual. Healthy solves land ~5e-6. The bound is set by what it replaces — the dense operator
    /// recovers these same bones to ~1e-6 — so a bone that cannot hold it widens to dense width by
    /// itself.</summary>
    const double SlimDefectGate = 1e-3;

    /// <summary>NaN-safe failure test: a NaN defect must FAIL a gate, never slip past a
    /// <c>&gt;</c> comparison into shipping unexamined coefficients.</summary>
    static bool FailsSlimGate(double defect) => !(defect <= SlimDefectGate);

    /// <summary>Is <paramref name="a"/> the better defect? NaN ranks below every value including infinity:
    /// a bare <c>&lt;</c> would let an unusable solve survive against a usable one, since every comparison
    /// with NaN is false. Equal defects keep the incumbent, so the search stays
    /// deterministic.</summary>
    internal static bool BetterDefect(double a, double b) => a < b || (double.IsNaN(b) && !double.IsNaN(a));

    /// <summary>The per-bone column-cap search levels, widest first (divisor of the selection size for the
    /// determinacy bound). See <see cref="PoolMath.LocalPInvRows"/>.</summary>
    static readonly int[] CapDivisors = { 4, 8, 16 };

    /// <summary>Anchor-row budget the slim search starts at, and the ceiling it doubles up to. Both are
    /// clamped to the vertex count.</summary>
    const int KStart = 32, KCap = 256;

    /// <summary>Co-weight below which a weak bone has no co-riding bone worth tying to, and the tie falls
    /// back to support-centroid proximity. A trace overlap names a bone the weak one barely touches, which
    /// the nearest sound support beats.</summary>
    const double TieCoWeightFloor = 0.01;

    /// <summary>Singular-value cutoff (relative to σmax) for every pseudoinverse the conditioning takes —
    /// the dense operator and each bone's local solve, which must truncate alike or the defect would
    /// measure a different system than the one it gates.</summary>
    const double OperatorRcond = 1e-8;

    static OperatorArt BuildOperator(StreamsLoad load, uint[] hashes, string name,
        IReadOnlyList<int>? retainedRows = null, bool classificationOnly = false, bool tieSlimFailures = false,
        IReadOnlyDictionary<uint, string>? bonePaths = null)
    {
        int nb = load.Nb, n = load.P.GetLength(0);
        var outputRows = retainedRows?.Distinct().OrderBy(i => i).ToArray()
            ?? Enumerable.Range(0, nb).ToArray();
        if (outputRows.Any(b => b < 0 || b >= nb))
            throw new ArgumentOutOfRangeException(nameof(retainedRows),
                $"operator rows must lie inside 0..{nb - 1}");
        if (outputRows.Length == 0)
            throw new ArgumentException("an emitted operator must retain at least one row", nameof(retainedRows));
        var C = PoolMath.BuildC(load.P, load.W, load.BI, nb);
        // Factored, not materialized: the m×n pinv is 8·m·n bytes and an m×m·n product away, and the only
        // consumers are one 3-column product and four rows per bone that widens. The whole matrix is formed
        // only when it SHIPS (the size verdict below).
        var pinv = PoolMath.Factor(C, OperatorRcond);

        // deterministic rigid-ish synthetic palette → per-bone recovery residual
        var T = new double[4 * nb, 3];
        for (int b = 0; b < nb; b++)
        {
            double a = 0.9 * b + 0.4, c = Math.Cos(a), s = Math.Sin(a);
            double[,] R = { { c, s, 0 }, { -s, c * 0.8, s * 0.6 }, { 0.1, -s * 0.6, c * 0.9 } };
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) T[4 * b + i, j] = R[i, j];
            T[4 * b + 3, 0] = 0.3 * Math.Sin(1.7 * b);
            T[4 * b + 3, 1] = 0.3 * Math.Cos(2.3 * b);
            T[4 * b + 3, 2] = 0.3 * Math.Sin(3.1 * b + 1);
        }
        var posed = new double[n, 3];
        for (int v = 0; v < n; v++)
            for (int j = 0; j < 3; j++)
            {
                double acc = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (load.W[v, k] <= 0) continue;
                    int b = load.BI[v, k];
                    acc += load.W[v, k] * (load.P[v, 0] * T[4 * b, j] + load.P[v, 1] * T[4 * b + 1, j]
                                         + load.P[v, 2] * T[4 * b + 2, j] + T[4 * b + 3, j]);
                }
                posed[v, j] = acc;
            }
        // against the operator AS SHIPPED (float32): an ill-conditioned row's rounding alone moves the
        // recovery by O(1), which is exactly what the gate below is set to catch.
        var recovered = pinv.ApplyAsFloat32(posed);          // 4·nb x 3
        var err = new double[nb];
        for (int b = 0; b < nb; b++)
            for (int r = 4 * b; r < 4 * b + 4; r++)
                for (int j = 0; j < 3; j++)
                    err[b] = Math.Max(err[b], Math.Abs(recovered[r, j] - T[r, j]));
        var weak = new bool[nb];
        for (int b = 0; b < nb; b++) weak[b] = err[b] > OperatorErrGate;

        var diagnostics = new List<string>();

        // ---- slim: per-bone anchor rows, gated on the LEFT-INVERSE DEFECT (pose-free — a single-pose
        // probe can pass a rank-truncated solve that misrecovers other palettes). K escalates part-wide,
        // re-solving only the bones still failing, until every dense-sound bone holds; one still failing at
        // the cap widens to every vertex by itself. K never exceeds the vertex count.
        // support centroids (weight-averaged bind positions) for the proximity fallback
        var centroid = new double[nb, 3];
        var wsum = new double[nb];
        for (int v = 0; v < n; v++)
            for (int k = 0; k < 4; k++)
            {
                double w = load.W[v, k];
                if (w <= 0) continue;
                int b = load.BI[v, k];
                wsum[b] += w;
                for (int j = 0; j < 3; j++) centroid[b, j] += w * load.P[v, j];
            }
        for (int b = 0; b < nb; b++)
            if (wsum[b] > 0)
                for (int j = 0; j < 3; j++) centroid[b, j] /= wsum[b];

        // The bone b rides when it cannot ship rows of its own: its strongest co-riding bone among those
        // `eligible` admits, or the nearest by support centroid when co-weight is negligible. -1 = none.
        int TieTarget(int b, Func<int, bool> eligible)
        {
            var co = new double[nb];
            for (int v = 0; v < n; v++)
            {
                double wb = 0;
                for (int k = 0; k < 4; k++) if (load.BI[v, k] == b && load.W[v, k] > wb) wb = load.W[v, k];
                if (wb <= 0) continue;
                for (int k = 0; k < 4; k++)
                {
                    int c2 = load.BI[v, k];
                    if (load.W[v, k] > 0 && c2 != b && eligible(c2)) co[c2] += wb * load.W[v, k];
                }
            }
            int best = -1;
            double bestScore = 0;
            for (int c2 = 0; c2 < nb; c2++) if (co[c2] > bestScore) { bestScore = co[c2]; best = c2; }
            if (bestScore < TieCoWeightFloor && wsum[b] > 0)
            {
                double bestD = double.MaxValue;
                for (int c2 = 0; c2 < nb; c2++)
                {
                    if (c2 == b || !eligible(c2) || wsum[c2] <= 0) continue;
                    double d = 0;
                    for (int j = 0; j < 3; j++) { double dd = centroid[b, j] - centroid[c2, j]; d += dd * dd; }
                    if (d < bestD) { bestD = d; best = c2; }
                }
            }
            return best;
        }

        // a weak bone rides a sound one; -1 = no sound bone anywhere on this mesh
        var tie = new int[nb];
        for (int b = 0; b < nb; b++) tie[b] = weak[b] ? TieTarget(b, c => !weak[c]) : b;

        // Palette planning consumes only the dense weak verdict, the rigid-tie sign/value, and bone hashes.
        // Stop before the slim selection/K-escalation and before a dense operator can be materialized.
        if (classificationOnly)
            return new OperatorArt(Array.Empty<float>(), outputRows.Select(b => weak[b]).ToArray(),
                outputRows.Select(b => tie[b]).ToArray(), outputRows.Select(b => hashes[b]).ToArray(),
                outputRows, Array.Empty<string>(), null, null, n);

        // Only retained outputs and the sound rows their rigid ties consume participate in the slim
        // search and its diagnostics. Every local solve still sees the full source column set.
        int kStart = Math.Max(1, Math.Min(KStart, n));
        int kCap = Math.Max(1, Math.Min(KCap, n));
        var slimRowsNeeded = outputRows.Concat(outputRows
                .Where(b => weak[b] && tie[b] >= 0)
                .Select(b => tie[b]))
            .Distinct().OrderBy(b => b).ToArray();
        var slim = SlimOperator(load, nb, kStart, kCap, weak, pinv, slimRowsNeeded);
        var picked = slim.Picked;
        var slimRows = slim.Rows;
        var slimErr = slim.Err;
        var denseWidth = slim.DenseWidth;

        // A sound bone no slim selection holds would ship every vertex of the mesh, and a recover row lasts
        // as long as its anchor list. With tieSlimFailures it rides a co-riding bone that IS held, the same
        // rigid tie a weak bone takes; only a bone with no such bone keeps the dense width. A stand-in the
        // search above did not solve is solved on its own first.
        var slimTie = Enumerable.Repeat(-1, nb).ToArray();
        if (tieSlimFailures)
        {
            var failing = slimRowsNeeded.Where(b => denseWidth[b]).ToHashSet();
            if (failing.Count > 0)
            {
                var unsolved = failing.OrderBy(b => b)
                    .Select(b => TieTarget(b, c => !weak[c] && !failing.Contains(c)))
                    .Where(t => t >= 0 && picked[t] is null).Distinct().OrderBy(t => t).ToArray();
                if (unsolved.Length > 0)
                {
                    var extra = SlimOperator(load, nb, kStart, kCap, weak, pinv, unsolved);
                    foreach (int t in unsolved)
                    {
                        picked[t] = extra.Picked[t];
                        slimRows[t] = extra.Rows[t];
                        slimErr[t] = extra.Err[t];
                        denseWidth[t] = extra.DenseWidth[t];
                    }
                }
                bool Held(int c) => !weak[c] && picked[c] is not null && !denseWidth[c];
                foreach (int b in failing.OrderBy(b => b)) slimTie[b] = TieTarget(b, Held);
            }
        }
        foreach (int b in slimRowsNeeded)
        {
            if (slimTie[b] < 0) continue;
            picked[b] = picked[slimTie[b]];
            slimRows[b] = slimRows[slimTie[b]];
            denseWidth[b] = false;
            tie[b] = slimTie[b];
        }
        // a weak bone whose stand-in just took a tie of its own rides where its stand-in now rides
        foreach (int b in outputRows)
            if (weak[b] && tie[b] >= 0 && slimTie[tie[b]] >= 0) tie[b] = slimTie[tie[b]];

        // The tie copies operator rows and (when slim) the anchor-vertex segment together — slim
        // coefficients are meaningless without the vertices they index — so it is applied to the SELECTION
        // before the widths are read off it, and the tied bone's block ends up the same width as its
        // target's.
        foreach (int b in outputRows)
        {
            if (!weak[b] || tie[b] < 0) continue;
            picked[b] = picked[tie[b]];
            slimRows[b] = slimRows[tie[b]];
        }

        // Slim ships when it is SMALLER, and that is the only verdict left: a bone the anchor-local solve
        // cannot hold rides a tie or widens to the whole mesh by itself, so no bone's conditioning can
        // decline the part.
        // Slim ships three buffers — 4 float rows of `width` per bone, the anchor indices those coefficients
        // are meaningless without, and the two-uint offset entry that locates both.
        long slimBytes = 8L * outputRows.Length;
        foreach (int b in outputRows) slimBytes += (16L + 4L) * picked[b].Length;
        long denseBytes = 16L * outputRows.Length * n;
        bool shipsSlim = slimBytes < denseBytes;

        foreach (int b in outputRows)
        {
            if (!weak[b] || tie[b] < 0) continue;
            int best = tie[b];
            // a tie inherits its target's width, and a target at the full vertex count makes the tied bone
            // cost 20·n bytes too — the size the rest of the message says nothing about
            string width = shipsSlim && picked[b].Length == n ? $" · at dense width ({n} rows)" : "";
            // the reported number is the dense residual — the verdict that produced the tie; the bone's
            // slim defect describes rows the tie overwrites, so it never ships
            diagnostics.Add($"{name}: bone {BoneName(bonePaths, hashes[b])} recovers ill-conditioned from this mesh "
                    + $"(err {err[b]:g2}) — tied rigidly to co-riding bone {BoneName(bonePaths, hashes[best])}{width}");
        }
        foreach (int b in outputRows)
            if (weak[b] && tie[b] < 0)
                diagnostics.Add($"{name}: bone {BoneName(bonePaths, hashes[b])} is weakly supported (err {err[b]:g2}) and has "
                        + "no sound bone to ride. Donor weight on it may distort");
        // the reported defect is the best slim solve's, the verdict that produced the tie
        foreach (int b in outputRows)
            if (slimTie[b] >= 0)
                diagnostics.Add($"{name}: no small set of vertices recovers bone {BoneName(bonePaths, hashes[b])} "
                        + $"(defect {slimErr[b]:g2} at K={slim.LastK}), so it is tied rigidly to co-riding bone "
                        + $"{BoneName(bonePaths, hashes[slimTie[b]])}");

        float[] op;
        uint[]? sel;
        uint[]? off;
        if (shipsSlim)
        {
            (op, sel, off) = AssembleSlim(picked, slimRows, outputRows);
            if (slim.LastK > kStart) diagnostics.Add($"{name}: anchor rows escalated to K={slim.LastK} to hold conditioning");
            foreach (int b in outputRows)
                if (slim.DenseWidth[b])
                    diagnostics.Add($"{name}: bone {BoneName(bonePaths, hashes[b])} ships at dense width — {picked[b].Length} rows "
                            + $"(defect {slimErr[b]:g2} at K={slim.LastK})");
            // what shipped next to what it replaced: a triager comparing a slim build against a dense one
            // needs both numbers. Bones whose rows the tie or the dense width replaced are not described by
            // their slim defect, so they are not candidates for the worst.
            int worst = -1;
            foreach (int b in outputRows)
                if (!weak[b] && slimTie[b] < 0 && !slim.DenseWidth[b]
                    && (worst < 0 || slimErr[b] > slimErr[worst])) worst = b;
            if (worst >= 0)
                diagnostics.Add($"{name}: slim operator ships · worst defect {slimErr[worst]:g2} (dense {err[worst]:g2})");
        }
        else
        {
            // a size verdict says nothing about conditioning, so it says nothing
            var dense = pinv.Materialize();
            op = new float[4 * outputRows.Length * n];
            for (int compact = 0; compact < outputRows.Length; compact++)
            {
                int b = outputRows[compact];
                int source = (weak[b] || slimTie[b] >= 0) && tie[b] >= 0 ? tie[b] : b;
                for (int r = 0; r < 4; r++)
                    Array.Copy(dense, (4 * source + r) * n, op, (4 * compact + r) * n, n);
            }
            sel = null;
            off = null;
        }
        return new OperatorArt(op, outputRows.Select(b => weak[b] || slimTie[b] >= 0).ToArray(),
            outputRows.Select(b => tie[b]).ToArray(), outputRows.Select(b => hashes[b]).ToArray(),
            outputRows, diagnostics, sel, off, n);
    }

    /// <summary>Whether the slim search holds <paramref name="bone"/> on this mesh, searched on its own. The
    /// search escalates each failing bone to the cap on its own account and solves it at each level from the
    /// bone and the level alone, so this is the verdict the full solve reaches for the bone beside any others.
    /// A dense-weak bone is never held.</summary>
    static bool HoldsSlim(StreamsLoad load, bool[] denseWeak, int bone)
    {
        if (denseWeak[bone]) return false;
        int n = load.P.GetLength(0);
        var slim = SlimOperator(load, load.Nb, Math.Max(1, Math.Min(KStart, n)), Math.Max(1, Math.Min(KCap, n)),
            denseWeak, densePinv: null, new[] { bone });
        return !FailsSlimGate(slim.Err[bone]);
    }

    /// <summary>Pack the per-bone selections and rows into the ragged triple the mod ships: bone b's block
    /// starts at element <c>base</c> of Sel and float <c>4*base</c> of Cpinv, is <c>width</c> wide, and
    /// <c>Off[2b], Off[2b+1]</c> carry the pair. Blocks tile both buffers in bone order with no padding and
    /// no gaps.</summary>
    static (float[] Cpinv, uint[] Sel, uint[] Off) AssembleSlim(int[][] picked, double[][][] rows,
        IReadOnlyList<int> retainedRows)
    {
        int total = 0;
        foreach (int b in retainedRows) total += picked[b].Length;
        var cp = new float[4 * total];
        var sel = new uint[total];
        var off = new uint[2 * retainedRows.Count];
        int bas = 0;
        for (int compact = 0; compact < retainedRows.Count; compact++)
        {
            int b = retainedRows[compact];
            int width = picked[b].Length;
            off[2 * compact] = (uint)bas;
            off[2 * compact + 1] = (uint)width;
            for (int t = 0; t < width; t++) sel[bas + t] = (uint)picked[b][t];
            for (int r = 0; r < 4; r++)
                for (int t = 0; t < width; t++)
                    cp[4 * bas + r * width + t] = (float)rows[b][r][t];
            bas += width;
        }
        return (cp, sel, off);
    }

    /// <summary>One part's per-bone slim selections and rows, before assembly. <see cref="Picked"/>[b] is
    /// the bone's anchor vertices (its row width), <see cref="Rows"/>[b] its four operator rows over them,
    /// <see cref="Err"/>[b] the defect of the solve those rows came from, and <see cref="DenseWidth"/>[b]
    /// whether the bone gave up on a narrow selection and took every vertex. <see cref="LastK"/> is the
    /// escalation level the search stopped at.</summary>
    sealed record SlimSolve(int[][] Picked, double[][][] Rows, double[] Err, bool[] DenseWidth, int LastK);

    /// <summary>The slim operator: per bone, up to K anchor vertices (weight-ranked, spread in bind space)
    /// and a local mass-restricted solve, gated on each bone's LEFT-INVERSE DEFECT (see
    /// <see cref="PoolMath.LocalPInvRows"/>). A bone failing at its level retries once with DISCRIMINATOR
    /// rows appended (see <see cref="PoolMath.SelectDiscriminatorRows"/>) before K escalates. K doubles from
    /// <paramref name="kStart"/> to <paramref name="kCap"/>, re-solving ONLY the dense-sound bones still
    /// failing; a bone keeps whichever solve has the smaller defect with its (possibly smaller) selection,
    /// and dense-weak bones solve once at kStart. A dense-sound bone still failing at the cap takes the
    /// DENSE width — every vertex, identity selection, the dense operator's own rows — instead of costing
    /// the rest of the part its slim widths. A bone with no support at all takes a single zero-coefficient
    /// row, which recovers the zero palette row the dense operator gives it. With no
    /// <paramref name="densePinv"/> the search only answers: failing bones keep their best slim attempt and
    /// <see cref="SlimSolve.Err"/> says they failed.</summary>
    static SlimSolve SlimOperator(StreamsLoad load, int nb, int kStart, int kCap, bool[] denseWeak,
        PoolMath.PInvFactors? densePinv, IReadOnlyList<int> activeRows)
    {
        int n = load.P.GetLength(0);
        var picked = new int[nb][];
        var rows = new double[nb][][];
        var err = new double[nb];
        var denseWidth = new bool[nb];

        // per-bone cap search, widest first: some bones need every strong co-bone kept, others condition
        // better with fewer near-dependent columns. First level that passes, else the best defect —
        // deterministic either way.
        (double[][] Rows, double Err) Solve(int b, int[] pk)
        {
            double[][]? bestRows = null;
            double bestErr = double.PositiveInfinity;
            foreach (int div in CapDivisors)
            {
                var (rr, dev) = PoolMath.LocalPInvRows(load.P, load.W, load.BI, b, pk, nb, div, OperatorRcond);
                if (bestRows is null || BetterDefect(dev, bestErr)) { bestRows = rr; bestErr = dev; }
                if (!FailsSlimGate(dev)) break;
            }
            return (bestRows!, bestErr);
        }

        int k = kStart;
        // One bone's level: its own selection, its own solve, its own slot in the three result arrays. No
        // bone reads another's, so the outcome is the same whatever order the level runs in. It runs SERIALLY
        // under the caller's fan-out over parts, which is what the CPU limit bounds — a parallel loop nested
        // inside a capped one multiplies past that bound rather than composing with it.
        void SolveBone(int b)
        {
            bool needs = picked[b] is null || (!denseWeak[b] && FailsSlimGate(err[b]));
            if (!needs) return;
            var pk = PoolMath.SelectAnchorRows(load.P, load.W, load.BI, b, k);
            if (pk.Length == 0)
            {
                picked[b] = new[] { 0 };
                rows[b] = new[] { new double[1], new double[1], new double[1], new double[1] };
                err[b] = double.PositiveInfinity;
                return;
            }
            var (bestRows, bestErr) = Solve(b, pk);
            if (FailsSlimGate(bestErr))
            {
                // the defect of a failing bone is usually its co-bones' contribution, which its own
                // vertices cannot separate out. Rows that pin the co-bones without carrying the bone can,
                // and cost only their own width.
                var disc = PoolMath.SelectDiscriminatorRows(load.P, load.W, load.BI, b, pk, nb, k);
                if (disc.Length > 0)
                {
                    var wide = pk.Concat(disc).ToArray();
                    Array.Sort(wide);
                    var (dRows, dErr) = Solve(b, wide);
                    if (BetterDefect(dErr, bestErr)) { bestRows = dRows; bestErr = dErr; pk = wide; }
                }
            }
            // a wider K that solves this bone WORSE keeps its narrower selection: the reported defect
            // and the rows it describes must be the same solve
            if (picked[b] is null || BetterDefect(bestErr, err[b]))
            {
                rows[b] = bestRows;
                err[b] = bestErr;
                picked[b] = pk;
            }
        }

        while (true)
        {
            foreach (int b in activeRows) SolveBone(b);
            bool ok = true;
            foreach (int b in activeRows) if (!(denseWeak[b] || !FailsSlimGate(err[b]))) { ok = false; break; }
            if (ok || k >= kCap) break;
            // A failing bone escalates to the cap on its own account, even past its own candidate count: its
            // discriminator budget grows with K, and a wider discriminator set is what separates a bone its
            // own vertices cannot. So a bone's verdict depends on nothing but the bone — the part's other
            // bones neither carry it further nor stop it sooner.
            k = Math.Min(k * 2, kCap);             // never overshoot the cap (nor the vertex count)
        }

        // a dense-sound bone the local solve never held widens to the whole mesh, taking the dense
        // operator's own rows — the ones the gate is calibrated against. Only this bone pays for it.
        var identity = (int[]?)null;
        foreach (int b in activeRows)
        {
            if (densePinv is null || denseWeak[b] || !FailsSlimGate(err[b])) continue;
            if (identity is null)
            {
                identity = new int[n];
                for (int v = 0; v < n; v++) identity[v] = v;
            }
            picked[b] = identity;
            var dr = new double[4][];
            for (int r = 0; r < 4; r++) dr[r] = densePinv.Row(4 * b + r);
            rows[b] = dr;
            denseWidth[b] = true;
        }
        return new SlimSolve(picked, rows, err, denseWidth, k);
    }

    // ---- mesh-dump readers -------------------------------------------------------------------------

    sealed record StreamsLoad(int Nb, double[,] P, double[,] W, int[,] BI);

    /// <summary>How much of this mesh each local bone actually poses, indexed by local bone: its summed
    /// positive vertex weight. Zero means the bone is a table entry moving nothing. The same quantity
    /// <see cref="PoolMath.BuildUnion"/> assigns palette ownership by and
    /// <see cref="StreamDump.WeightedBoneHashes"/> reads off a bundle's mesh field, so a caller reasoning
    /// about whether a bone's palette row would be WRITTEN reads what the writer does. An index outside the
    /// bone table poses nothing here: the union carries no slot for it, so it can own no row.</summary>
    static double[] SummedWeights(StreamsLoad load)
    {
        var summed = new double[load.Nb];
        for (int v = 0; v < load.W.GetLength(0); v++)
            for (int k = 0; k < 4; k++)
            {
                int b = load.BI[v, k];
                if (load.W[v, k] > 0 && b >= 0 && b < summed.Length) summed[b] += load.W[v, k];
            }
        return summed;
    }

    static StreamsLoad LoadStreams(string dir, Matrix4x4? conversion)
    {
        var s0 = ReadVertexStream(dir, conversion);
        var s2 = File.ReadAllBytes(Path.Combine(dir, "stream2.buf"));
        int nb = BoneCount(dir);
        var p = PoolMath.ParsePositions(s0, 40, 0);
        var (w, bi) = PoolMath.ParseSkin(s2);
        if (w.GetLength(0) != p.GetLength(0))
            throw new InvalidDataException(
                $"skin rows ({w.GetLength(0)}) don't match position rows ({p.GetLength(0)}) in '{dir}' — "
                + "the dumped mesh doesn't carry the float4-weight/uint4-index skin stream");
        return new StreamsLoad(nb, p, w, bi);
    }

    static PoolMath.UnionInput LoadUnionInput(string dir, Matrix4x4? conversion)
    {
        var (hashes, binds) = ReadBindpose(dir);
        if (conversion is { } d) binds = Rebase(binds, d);
        var s2 = File.ReadAllBytes(Path.Combine(dir, "stream2.buf"));
        return new PoolMath.UnionInput(hashes, binds, s2);
    }

    /// <summary>An operator with each output bone's bind constant folded in. A recovered row is the
    /// bind-included skin matrix, so restating it under the reference bind is <c>M' = C · M</c>: output
    /// bone i's four operator rows become <c>row'_r = Σ_s C[r,s] · row_s</c>, coefficient by coefficient.
    /// The product is taken in double and rounded once to the width the operator ships at.
    /// <paramref name="constants"/> is indexed like <see cref="OperatorArt.Hashes"/>; null = that bone's
    /// rows ship as solved.</summary>
    internal static float[] FoldConstants(OperatorArt art, IReadOnlyList<double[]?> constants)
    {
        var op = (float[])art.Cpinv.Clone();
        var y = new double[4];
        for (int i = 0; i < constants.Count; i++)
        {
            if (constants[i] is not { } c) continue;
            // the ragged slim layout files bone i at (base, width); the dense one at i·4·N, N wide
            int width = art.Off is { } off ? (int)off[2 * i + 1] : art.N;
            int at = art.Off is { } off2 ? 4 * (int)off2[2 * i] : 4 * i * art.N;
            for (int t = 0; t < width; t++)
            {
                for (int s = 0; s < 4; s++) y[s] = art.Cpinv[at + s * width + t];
                for (int r = 0; r < 4; r++)
                    op[at + r * width + t] = (float)(c[r * 4] * y[0] + c[r * 4 + 1] * y[1]
                                                     + c[r * 4 + 2] * y[2] + c[r * 4 + 3] * y[3]);
            }
        }
        return op;
    }

    static PoolMath.IdentityPart LoadIdentityPart(string dir, Matrix4x4? conversion) => new(
        ReadVertexStream(dir, conversion),
        File.ReadAllBytes(Path.Combine(dir, "stream1.buf")),
        File.ReadAllBytes(Path.Combine(dir, "stream2.buf")),
        File.ReadAllBytes(Path.Combine(dir, "ib.buf")));

    /// <summary>The dump's stream0, restated in the pipeline's reference bind space. stream1 (colour/UV)
    /// and stream2 (weights/indices) carry nothing directional, so only this stream converts.</summary>
    static byte[] ReadVertexStream(string dir, Matrix4x4? conversion)
    {
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        return conversion is { } d ? PoolMath.RotateVertexStream(s0, d) : s0;
    }

    /// <summary>Every bindpose restated in the reference space, keyed as read.</summary>
    static Dictionary<uint, double[]> Rebase(IReadOnlyDictionary<uint, double[]> binds, Matrix4x4 delta)
    {
        var outb = new Dictionary<uint, double[]>(binds.Count);
        foreach (var (h, bp) in binds)
            outb[h] = BindSpace.ToRowMajor(BindSpace.Rebase(BindSpace.FromRowMajor(bp), delta));
        return outb;
    }

    static int BoneCount(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bindpose.json")));
        return doc.RootElement.GetProperty("boneCount").GetInt32();
    }

    /// <summary>A mesh dump's bone table, in the order its skin indices name the bones.</summary>
    internal static uint[] DumpBoneHashes(string dir) => ReadBindpose(dir).Hashes;

    static (uint[] Hashes, Dictionary<uint, double[]> Binds) ReadBindpose(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bindpose.json")));
        var bones = doc.RootElement.GetProperty("bones");
        var hashes = new List<uint>();
        var binds = new Dictionary<uint, double[]>();
        foreach (var b in bones.EnumerateArray())
        {
            uint h = (uint)b.GetProperty("hash").GetInt64();
            var bp = b.GetProperty("bindpose").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            hashes.Add(h);
            // a bone a table lists twice is read at its FIRST entry, as the donor compile and the bind
            // reference read it
            binds.TryAdd(h, bp);
        }
        return (hashes.ToArray(), binds);
    }

    // ---- bind-space reconciliation -----------------------------------------------------------------

    /// <summary>Each dump dir's conversion into its pipeline's REFERENCE bind space, absent where the dump
    /// already is in it — the dumps adapted into <see cref="SwapCompile.ReferenceConversions"/>, which the
    /// donor compile reaches from the bundle fields, so donor streams and palette state one union space.
    /// Scene-rest space is a property of the SUBJECT, so every pipeline converts a shared dump the same
    /// way; a tier fits against its own lod0 as authored and composes the lod0's conversion. A dir gets no
    /// entry when the delta is not one uniform rigid rotation: bone-name hashes collide across unrelated
    /// rigs, and the union and tier gates must keep refusing those.</summary>
    /// <param name="anchors">Each pipeline's already-resolved anchor index, positionally against
    /// <c>req.Pipelines</c> — the refusal for an anchor the pool doesn't carry has fired before this
    /// runs.</param>
    static Dictionary<string, Matrix4x4> BindConversions(PoolBuildRequest req, IReadOnlyList<int> anchors)
    {
        var read = new Dictionary<string, (uint[] Hashes, Dictionary<uint, double[]> Binds)>(StringComparer.Ordinal);
        (uint[] Hashes, Dictionary<uint, double[]> Binds) Raw(string dir) =>
            read.TryGetValue(dir, out var b) ? b : read[dir] = ReadBindpose(dir);

        var conversion = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        // One dump carries one space, so two pipelines that would restate it differently cannot both build.
        void Settle(string dir, Matrix4x4? delta)
        {
            var had = conversion.TryGetValue(dir, out var have) ? have : (Matrix4x4?)null;
            if (!settled.Add(dir) && had != delta)
                throw new InvalidOperationException(
                    $"dump '{dir}' is pooled by two pipelines whose reference bind spaces differ. One dump "
                    + "holds one space, so these Replaces can't build together");
            if (delta is { } d) conversion[dir] = d;
        }

        for (int pipeIdx = 0; pipeIdx < req.Pipelines.Count; pipeIdx++)
        {
            var pipe = req.Pipelines[pipeIdx];
            var parts = pipe.Parts.Select(p => p.Name).ToList();
            var dirs = pipe.Parts.Select(p => p.DumpDir).ToList();
            int anchorIdx = anchors[pipeIdx];

            var deltas = SwapCompile.ReferenceConversions(
                pipe.Parts.Select(p => BindPartOf(Raw(p.DumpDir), p.MeasuredRest)).ToList(), anchorIdx);
            for (int i = 0; i < dirs.Count; i++) Settle(dirs[i], deltas[i]);

            // A tier fits against its own lod0 AS AUTHORED — where a same-space tier is an identity fit
            // regardless of how few bones survived decimation — and the lod0's conversion composes on
            // top, the same shape as the parts above.
            foreach (var t in pipe.Tiers ?? Array.Empty<PoolTier>())
            {
                int pi = parts.IndexOf(t.Part);
                if (pi < 0) continue;                          // the emission raises its own refusal for this
                var lodConv = conversion.TryGetValue(dirs[pi], out var pd) ? pd : (Matrix4x4?)null;
                Settle(t.DumpDir, SwapCompile.Compose(
                    SwapCompile.FittedDelta(BindPartOf(Raw(t.DumpDir), null), BindPartOf(Raw(dirs[pi]), null)),
                    lodConv));
            }

            // A wardrobe-group member is restated exactly as a POOL PART is — against the anchor, by the
            // parts' measured rests where both carry one and by a fitted delta otherwise — because its
            // recovered rows pose donor vertices the union states in that one space. Its own tiers then fit
            // against its lod0 and compose, the shape the pool tiers take above.
            foreach (var (_, member, mesh) in GroupParts(pipe))
            {
                if (mesh.IsLod0)
                {
                    Settle(mesh.DumpDir, SwapCompile.ReferenceConversions(new[]
                    {
                        BindPartOf(Raw(mesh.DumpDir), member.MeasuredRest),
                        BindPartOf(Raw(dirs[anchorIdx]), pipe.Parts[anchorIdx].MeasuredRest),
                    }, 1)[0]);
                    continue;
                }
                var lod0 = (member.Meshes ?? Array.Empty<PoolGroupMesh>()).FirstOrDefault(x => x.IsLod0);
                if (lod0 is null) continue;               // the emission raises its own refusal for this
                Settle(mesh.DumpDir, SwapCompile.Compose(
                    SwapCompile.FittedDelta(BindPartOf(Raw(mesh.DumpDir), null),
                        BindPartOf(Raw(lod0.DumpDir), null)),
                    conversion.TryGetValue(lod0.DumpDir, out var md) ? md : (Matrix4x4?)null));
            }
        }
        return conversion;
    }

    /// <summary>Every captured draw of every wardrobe-group member this pipeline carries, in group then
    /// member then mesh order — the one enumeration the operator solve, the bind-space settlement and the
    /// emission all walk, so none of them can reach a mesh another skipped.</summary>
    static IEnumerable<(PoolGroup Group, PoolGroupMember Member, PoolGroupMesh Mesh)> GroupParts(
        ReplacePipeline pipe)
    {
        foreach (var g in pipe.Groups ?? Array.Empty<PoolGroup>())
            foreach (var m in g.Members)
                foreach (var mesh in m.Meshes ?? Array.Empty<PoolGroupMesh>())
                    yield return (g, m, mesh);
    }

    /// <summary>The same walk, meshes alone.</summary>
    static IEnumerable<PoolGroupMesh> GroupMeshes(ReplacePipeline pipe) =>
        GroupParts(pipe).Select(x => x.Mesh);

    /// <summary>A dump's bindposes in the shape the shared bind-space composition reads, row-major json
    /// floats decoded to row-vector matrices on demand.</summary>
    static SwapCompile.BindPart BindPartOf((uint[] Hashes, Dictionary<uint, double[]> Binds) raw,
        Matrix4x4? measuredRest) =>
        new(raw.Hashes, h => raw.Binds.TryGetValue(h, out var bp) ? BindSpace.FromRowMajor(bp) : null,
            measuredRest);

    static byte[] FloatBytes(float[] a)
    {
        var b = new byte[a.Length * 4];
        Buffer.BlockCopy(a, 0, b, 0, b.Length);
        return b;
    }

    static byte[] UIntBytes(uint[] a)
    {
        var b = new byte[a.Length * 4];
        Buffer.BlockCopy(a, 0, b, 0, b.Length);
        return b;
    }
}
