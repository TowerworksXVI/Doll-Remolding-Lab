using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Remold.Core.Mesh;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>One pooled part: the identifier used in filenames and ini sections, its mesh-dump dir, and
/// <paramref name="OpKey"/> — the source mesh's stable game-side identity for operator-cache reuse (null =
/// solve fresh, keep nothing). <paramref name="MeasuredRest"/> is the part's measured bind→scene transform
/// (see <see cref="Skeleton.SceneRig.MeasuredRest"/>); the bind-space reconciliation MUST prefer measured
/// deltas exactly as <see cref="SwapCompile.BuildUnionOrder"/> does — the compiled donor streams and the
/// emitted palette state one union space.</summary>
public readonly record struct PoolPart(string Name, string DumpDir, string? OpKey = null,
    System.Numerics.Matrix4x4? MeasuredRest = null);

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

    /// <summary>Ordered pool parts. The last is the anchor unless <see cref="Anchor"/> overrides. Max 8
    /// (the convert pass uses cbuffer slots b5..b12 for parts, b13 for the anchor).</summary>
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

    /// <summary>Pool parts' other LOD tiers. A suppressed part's tier is replaced the same way as its
    /// lod0 — LOD choice is not distance-only, so a merely-hidden tier would blank the character in every
    /// context that picks it. Each tier gets its own capture (skip) + recovery operator, and the anchor's
    /// tiers each run the full recover→convert→skin→draw chain. A NoSkip part's tier is captured WITHOUT
    /// skip: in a frame rendering only that tier the part's lod0 capture never fires, and an uncaptured
    /// recovery input would pose its owned bones with garbage. Tier chains use the constants-free WITNESS
    /// convert: tier renderers can draw at per-part spaces differing from lod0's, and their vs-cb1 can be
    /// a window into a shared buffer a resource copy reads wrongly, so no CB is captured for tiers.</summary>
    public IReadOnlyList<PoolTier>? Tiers { get; init; }

    /// <summary>This Replace's own toggle key (tier 2). Null = no key, and the pipeline's suppression and
    /// draw run unconditionally — the emission that predates keys. See <see cref="ModKeys"/>.</summary>
    public string? ToggleKey { get; init; }

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

    /// <summary>This member's own draws are SUPPRESSED by the build, the way a hidden pool part's are —
    /// the capture and the rebase still run at them, which is what a hidden recovery source has always
    /// done. Default is the ordinary member, whose vanilla draw keeps rendering.</summary>
    public bool Hidden { get; init; }
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

/// <summary>
/// One RIGID replacement: a direct geometry swap at a draw the game does not pose per vertex. The compiled
/// donor's streams stand in for the vanilla ones and its submeshes are drawn in their place — no capture,
/// no palette recovery, no compute pass. Everything is per-draw; nothing is shared between replacements.
/// </summary>
public sealed record RigidReplace
{
    /// <summary>Names this replacement's shipped resources; unique across the whole build.</summary>
    public required string Suffix { get; init; }

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

    /// <summary>Per-donor-submesh texture binds; null when every submesh keeps the part's stock maps.</summary>
    public IReadOnlyDictionary<int, SubmeshMaps>? SubTextures { get; init; }

    /// <summary>The replaced part's own stock maps, tagged so the draw's slot probe can find them.</summary>
    public IReadOnlyList<StockMapTag>? StockMaps { get; init; }

    /// <summary>This change's tier-2 toggle key, or null when it carries none.</summary>
    public string? ToggleKey { get; init; }

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
    /// a hash appears in exactly one TextureOverride section of the emitted ini.</summary>
    public IReadOnlyList<string>? HideHashes { get; init; }

    /// <summary>Retexture sections appended after the pooled emission. See
    /// <see cref="RetexEntry"/>.</summary>
    public IReadOnlyList<RetexEntry>? Retextures { get; init; }

    /// <summary>Draw-scoped retexture sections appended after the pooled emission. See
    /// <see cref="ScopedRetexEntry"/>.</summary>
    public IReadOnlyList<ScopedRetexEntry>? ScopedRetextures { get; init; }

    /// <summary>The presence latches this build's latched edits reference, one per authored outfit
    /// whose edits need one. See <see cref="WitnessLatch"/>.</summary>
    public IReadOnlyList<WitnessLatch>? Latches { get; init; }

    /// <summary>Per-hide presence latch, by the hide's ib hash. A hash with no entry hides whenever its
    /// keys allow.</summary>
    public IReadOnlyDictionary<string, string>? HideLatches { get; init; }

    /// <summary>The mod's own toggle key (tier 1): every suppression, draw and texture override in the
    /// emitted ini is gated on it, so one key turns the whole mod off. Null = no key, and the mod is always
    /// on. See <see cref="ModKeys"/>.</summary>
    public string? ToggleKey { get; init; }

    /// <summary>Per-hide toggle key, by the hide's own ib hash (tier 2). A hash with no entry hides
    /// unconditionally.</summary>
    public IReadOnlyDictionary<string, string>? HideKeys { get; init; }

    /// <summary>The keys whose variable is declared 0, so what they gate starts OFF and the first press
    /// turns it on. A key not listed starts on. See <see cref="MigotoEmitter.KeyDeclarations"/>.</summary>
    public IReadOnlyCollection<string>? KeysStartingOff { get; init; }

    /// <summary>Guards for the sections whose hash also fires on a sibling mesh's draws, one per guarded
    /// hash. See <see cref="TwinGuard"/>.</summary>
    public IReadOnlyList<TwinGuard>? TwinGuards { get; init; }

    /// <summary>The external writers of the guards' sticky variables — sections that identify a sibling
    /// by drawing at all rather than by a texture bound at the guarded draw. See
    /// <see cref="TwinSighting"/>.</summary>
    public IReadOnlyList<TwinSighting>? TwinSightings { get; init; }
}

/// <summary>One submesh draw of a replaced mesh as the game issues it: the draw's start index and index
/// count. A multi-material mesh is drawn once per material, each draw covering one submesh's index
/// range, and those two values are what a section can match a specific material's draw on.</summary>
public readonly record struct DrawShape(int First, int Count);

/// <summary>A replaced mesh's vanilla draw shapes: one <see cref="DrawShape"/> per submesh in submesh
/// order, plus the full index count for a pass that draws the whole mesh in one call.</summary>
public sealed record DrawShapeSet(IReadOnlyList<DrawShape> Shapes, int FullCount);

/// <summary>One replaced LOD tier of a pool part: <paramref name="Part"/> is the pool part name,
/// <paramref name="Name"/> the unique emission name, <paramref name="Suffix"/> the tier level key. The
/// anchor's tier chain pairs every part's same-suffix tier, falling back to the part's lod0 recover for
/// parts without that tier (whose lod0 capture refs stay current: buffers upload at frame start).
/// <paramref name="OpKey"/> is the source mesh's stable identity, as on <see cref="PoolPart"/>.
/// <paramref name="Shapes"/> (anchor tiers only) is the tier mesh's vanilla draw-shape set; null keeps
/// the draw in the capture section.</summary>
public sealed record PoolTier(string Part, string Name, string Suffix, string DumpDir, string CaptureHash,
    string? OpKey = null, DrawShapeSet? Shapes = null);

/// <summary>
/// One retextured stock texture. <paramref name="Name"/> is the section suffix (unique per entry);
/// <paramref name="Hash"/> is the STOCK texture's own 8-hex 3DMigoto resource hash;
/// <paramref name="DdsFile"/> is the replacement's source path (DDS, already encoded).
///
/// <para>The override keys on the texture resource, not on any draw, so one section covers every pass,
/// environment and LOD tier with no slot knowledge. The reach is game-wide — any mesh sampling that
/// texture is retextured, and two mods editing the same stock texture collide by construction.</para>
///
/// <para><paramref name="ToggleKey"/> is this retexture's own key (tier 2); null = always on.</para>
/// </summary>
public sealed record RetexEntry(string Name, string Hash, string DdsFile, string? ToggleKey = null);

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
    string? ToggleKey = null);

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
    string Part = "");

/// <summary>
/// One outfit's presence latch. A sighting of any <paramref name="WitnessIbs"/> draw records into
/// <c>$zz_seen_{Name}</c>; <c>[Present]</c> commits it into <c>$zz_gate_{Name}</c> and clears it, so a
/// latched edit tests LAST frame's verdict — constant across every draw and pass of the current frame. An
/// edit on a shared anchor thus applies exactly while the authored outfit is on screen; when two wearers
/// co-draw, both show it (only the authored outfit's own witnesses are consulted).
/// </summary>
public sealed record WitnessLatch(string Name, IReadOnlyList<string> WitnessIbs);

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

/// <summary>One submesh's three map slots at its own draw. A submesh whose every slot inherits binds
/// nothing, which is what an untouched vanilla submesh of a remolded pipeline wants.</summary>
public sealed record SubmeshMaps(MapSlot Albedo = default, MapSlot Normal = default, MapSlot Rmo = default)
{
    /// <summary>No slot of this submesh binds anything.</summary>
    public bool BindsNothing => Albedo.IsInherit && Normal.IsInherit && Rmo.IsInherit;
}

/// <summary>Which map a stock texture is, for the draw's slot probe.</summary>
public enum StockMapKind { Albedo, Normal, Rmo }

/// <summary>One stock texture of a Replace anchor: its 8-hex 3DMigoto resource hash and map kind. The
/// emitter tags it with a kind-specific <c>filter_index</c>; the draw command list probes
/// <c>ps-t0..t6</c> for those indices to find the live slots.
///
/// <para><paramref name="Part"/> is the anchor part as the change list labels it, carried so a refusal over
/// this hash can name a row the author can find. Empty when the caller has no label.</para></summary>
public sealed record StockMapTag(string Hash, StockMapKind Kind, string Part = "");

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
    /// <summary>Where this build may keep solved operators (see <see cref="OperatorCachePath"/>).
    /// Null = solve fresh, write nothing.</summary>
    public string? OperatorCacheDir { get; init; }

    /// <summary>Most cores the operator solve may spread across (see <see cref="SolveOperators"/>). Null =
    /// every logical processor.</summary>
    public int? CpuLimit { get; init; }

    // The draw probes these ps-t slots for the anchor's stock maps. 0..6 covers every slot layout
    // measured across environments (albedo at t0..t3; normal/RMO up to t6); the probe reads the slot
    // actually bound at draw time, so an unmeasured environment costs nothing but a slot in this range.
    static readonly int[] ProbeSlots = { 0, 1, 2, 3, 4, 5, 6 };

    // filter_index values for the slot tags — distinctive on purpose: a texture's probe answer is the
    // HIGHEST-priority filter_index among every ini's sections on that hash, so a third-party mod tagging
    // the same stock texture with a common small value would be indistinguishable from ours.
    internal const int FilterAlbedo = 3301, FilterNormal = 3302, FilterRmo = 3303;

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
        VarRmoSlot = "zz_slot_r";

    // The scoped-retexture sections' own probe scratch + found-slot variable, separate from the draw
    // list's so a scoped bind can never clobber a Replace draw's probe state mid-frame.
    const string VarRetexProbe = "zz_rt", VarRetexSlot = "zz_rslot";

    // The scratch a multi-verdict twin guard folds its verdicts into: the ini nests if/endif rather than
    // offering an OR, so the admitted verdicts each set this and the body opens on it once. Declared only
    // in builds that carry such a guard.
    const string VarTwinOk = "zz_twok";

    /// <summary>Pool parts one pipeline can carry — the convert shader's cb register range
    /// (<see cref="ComputeTemplates.MaxPartCBuffers"/>) and nothing about taste. Raising it means finding
    /// the shader more registers.</summary>
    public const int MaxPoolParts = ComputeTemplates.MaxPartCBuffers;

    /// <summary>The slot-tag filter value carried for a stock map kind.</summary>
    static int KindFilter(StockMapKind kind) => kind switch
    {
        StockMapKind.Albedo => FilterAlbedo,
        StockMapKind.Normal => FilterNormal,
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
        : kind switch { StockMapKind.Albedo => m.Albedo, StockMapKind.Normal => m.Normal, _ => m.Rmo };

    /// <summary>Any draw of this replacement binds a texture slot, so the draw list needs the probe and the
    /// ps-t save/restore around it.</summary>
    static bool DonorTexed(SubmeshMaps?[] subMaps) => subMaps.Any(m => m is { BindsNothing: false });

    /// <summary>Any draw of this replacement asks for the shipped flat map of <paramref name="kind"/>.</summary>
    static bool UsesNeutral(SubmeshMaps?[] subMaps, StockMapKind kind) =>
        subMaps.Any(m => m is not null && kind switch
        {
            StockMapKind.Normal => m.Normal.IsNeutral,
            StockMapKind.Rmo => m.Rmo.IsNeutral,
            _ => false,
        });

    /// <summary>The variables a block is gated on: the mod's tier-1 key, then the change's tier-2 key, via
    /// <see cref="ModKeys.VariableFor"/>, emitted as nested <c>if $v == 1</c> blocks. An EMPTY gate emits
    /// nothing at all: an unkeyed mod's ini is byte-identical to the emission that predates keys. Two
    /// changes bound to one key share one variable and toggle together (the build warns).</summary>
    readonly struct Gate
    {
        public readonly string[] Vars;

        public Gate(params string?[] keys) : this(keys, null) { }

        /// <summary><paramref name="rawVars"/> are pre-made variable NAMES (a presence latch's gate
        /// variable), appended after the key variables — the same <c>if $v == 1</c> substrate.</summary>
        public Gate(IEnumerable<string?> keys, IEnumerable<string?>? rawVars)
        {
            var vars = new List<string>();
            foreach (var k in keys)
                if (ModKeys.Normalize(k) is { } n)
                {
                    var v = ModKeys.VariableFor(n);
                    if (!vars.Contains(v, StringComparer.Ordinal)) vars.Add(v);
                }
            foreach (var v in rawVars ?? Array.Empty<string?>())
                if (!string.IsNullOrEmpty(v) && !vars.Contains(v!, StringComparer.Ordinal)) vars.Add(v!);
            Vars = vars.ToArray();
        }

        /// <summary>Nothing gates this block — emit it bare.</summary>
        public bool IsAlwaysOn => Vars.Length == 0;

        /// <summary>The gate's identity, for deduping two contributions that gate identically.</summary>
        public string Id => string.Join('|', Vars);

        public void Open(StringBuilder p) { foreach (var v in Vars) p.Append($"if ${v} == 1\n"); }
        public void Close(StringBuilder p) { for (int i = 0; i < Vars.Length; i++) p.Append("endif\n"); }

        /// <summary>The gate's lines wrapped around <paramref name="body"/>, as a list of ini lines — for
        /// the capture units, which collect lines rather than write straight to a builder.</summary>
        public IEnumerable<string> Wrap(IEnumerable<string> body)
        {
            foreach (var v in Vars) yield return $"if ${v} == 1";
            foreach (var line in body) yield return line;
            for (int i = 0; i < Vars.Length; i++) yield return "endif";
        }
    }

    /// <summary><paramref name="UnionBones"/>/<paramref name="VertexCount"/> are totals across pipelines.
    /// <paramref name="Warnings"/> are user-facing and actionable; <paramref name="Diagnostics"/> record
    /// what the emission did, reaching the build log and no UI surface.</summary>
    public readonly record struct Result(string OutDir, int UnionBones, int VertexCount,
        IReadOnlyList<string> Warnings, IReadOnlyList<string> Diagnostics);

    sealed record PipelineEmission(string Sfx,
        List<(string Part, int N, int Nb, int Rows)> PartMeta, int AnchorIdx,
        IReadOnlyDictionary<string, string> CapHashes, int Ub, int Vcount, int Vb1Stride, string IbFmt,
        List<(int Count, int Start, int Base)> Draws, SubmeshMaps?[] SubMaps,
        HashSet<string>? NoSkip, List<(string Part, string Name, string Suffix, string Hash, int Rows, DrawShapeSet? Shapes)> TierMeta,
        bool Lod0WitnessConvert, string? ToggleKey, string? Latch, bool HideWhenOff, List<GroupMemberEmission> GroupMembers,
        List<GroupMemberClaim> GroupClaims, List<(string Part, int Pairs)> Ties,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? PresenceHashes, DrawShapeSet? AnchorShapes);

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
    sealed record GroupMemberClaim(string Name, string Hash, bool Hidden);

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

    /// <summary>Derive the tie underlay and write its shaders: for every donor-WEIGHTED union bone another
    /// part owns, the deepest skeleton ancestor the anchor owns — anchor-owned rows are recovered at the
    /// anchor's own draw, so the ancestor's converted row is live whenever the replacement is. A verbatim
    /// row copy is the rigid ride (rows are combined bind→posed affines; the bind-relative delta cancels).
    /// Bones with no path or no anchor-owned ancestor keep the identity seed, named in the build log —
    /// bind-pose placement, strictly tamer than the unwritten-recover garbage the gate retired. One shader
    /// per owner part, pairs in ascending tied-slot order, parts in pool order: rebuilds reproduce.</summary>
    static List<(string Part, int Pairs)> TieUnderlay(string outDir, string sfx, PoolMath.UnionResult union,
        int anchorIdx, List<string> parts, HashSet<int> donorSlots,
        IReadOnlyDictionary<uint, string>? bonePaths, List<string> diagnostics)
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
                diagnostics.Add($"{sfx}: bone 0x{hash:x8} has no skeleton path — donor weight on it keeps "
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
                diagnostics.Add($"{sfx}: bone 0x{hash:x8} has no anchor-owned skeleton ancestor — donor "
                    + $"weight on it keeps the bind-pose seed while '{owner}' is absent");
                continue;
            }
            if (!pairsByOwner.TryGetValue(union.Owner[u], out var list))
                pairsByOwner[union.Owner[u]] = list = new List<(uint, uint)>();
            list.Add(((uint)u, (uint)best));
            diagnostics.Add($"{sfx}: bone 0x{hash:x8} rides its ancestor 0x{union.UnionHashes[best]:x8} "
                + $"rigidly while '{owner}' is absent");
        }
        foreach (int owner in pairsByOwner.Keys.Concat(seedsByOwner.Keys).Distinct().OrderBy(k => k))
        {
            var pairs = pairsByOwner.GetValueOrDefault(owner) ?? new List<(uint, uint)>();
            var seeds = seedsByOwner.GetValueOrDefault(owner) ?? new List<uint>();
            File.WriteAllText(Path.Combine(outDir, $"tiefill_{parts[owner]}_{sfx}.hlsl"),
                ComputeTemplates.EmitTieFill(pairs, seeds));
            ties.Add((parts[owner], pairs.Count + seeds.Count));
        }
        return ties;
    }

    /// <summary>Move a compiled donor's group-bone indices off the dense continuation of the union and onto
    /// the palette slots the emission reserved for them: <c>unionBones + k</c> becomes
    /// <c>groupBase + k</c>, in place. An index past the continuation is left alone — the range warning
    /// above is what names it.</summary>
    static void ShiftGroupIndices(byte[] skin, int unionBones, uint groupBase, int groupBones)
    {
        int shift = (int)groupBase - unionBones;
        if (shift == 0) return;
        for (int o = 16; o + 16 <= skin.Length; o += 32)
            for (int k = 0; k < 4; k++)
            {
                uint bi = BitConverter.ToUInt32(skin, o + k * 4);
                if (bi >= (uint)unionBones && bi < (uint)(unionBones + groupBones))
                    BitConverter.GetBytes((uint)(bi + shift)).CopyTo(skin, o + k * 4);
            }
    }

    public Result Build(PoolBuildRequest req)
    {
        var warnings = new List<string>();
        var diagnostics = new List<string>();
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
        var solved = SolveOperators(req, Load, UnionInput, Conv);
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
                    ComputeTemplates.EmitRecover(4 * Load(dir).Nb));
            }
            else
            {
                File.WriteAllText(Path.Combine(req.OutDir, $"recover_{name}_cs.hlsl"),
                    ComputeTemplates.EmitRecoverDense(a.N, 4 * Load(dir).Nb));
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
                if (kv.Key < 0 || kv.Key >= drawCount)
                {
                    warnings.Add($"{sfx}: texture for submesh {kv.Key} is out of range ({drawCount} submeshes). Skipped");
                    continue;
                }
                subMaps[kv.Key] = new SubmeshMaps(Ship(kv.Value.Albedo), Ship(kv.Value.Normal), Ship(kv.Value.Rmo));
            }
            return subMaps;
        }

        var pipes = new List<PipelineEmission>();
        int ubTotal = 0, vcountTotal = 0;

        for (int pipeIdx = 0; pipeIdx < req.Pipelines.Count; pipeIdx++)
        {
            var pipe = req.Pipelines[pipeIdx];
            string sfx = pipe.Suffix;
            var parts = pipe.Parts.Select(p => p.Name).ToList();
            var dirs = pipe.Parts.Select(p => p.DumpDir).ToList();
            if (parts.Count > MaxPoolParts)
                throw new InvalidOperationException(
                    $"{sfx}: convert pass uses cb slots b5..b12 for parts (b13 = anchor) — {MaxPoolParts} pool parts max");
            int anchorIdx = anchorOf[pipeIdx];
            var capHashes = pipe.CaptureHashes ?? new Dictionary<string, string>();
            var subTexOverrides = pipe.SubTextures ?? new Dictionary<int, SubmeshMaps>();

            // ---- union reconciliation (single union-order authority: first-seen across the pool) ------
            var unionInputs = dirs.Select(UnionInput).ToList();
            var union = PoolMath.BuildUnion(unionInputs);
            int ub = union.UnionHashes.Length;
            ubTotal += ub;

            // ---- per-part shared operator + per-pipeline scatter map ----------------------------------
            // (map files are written AFTER witness selection below — witnesses repurpose entries)
            var partMeta = new List<(string Part, int N, int Nb, int Rows)>();
            var partScatter = new List<uint[]>();
            var partArts = new List<OperatorArt>();
            for (int i = 0; i < parts.Count; i++)
            {
                ClaimName(parts[i], dirs[i]);
                var load = Load(dirs[i]);
                partArts.Add(Operator(parts[i], dirs[i]));   // conditioning + rigid ties, once per part
                partMeta.Add((parts[i], load.P.GetLength(0), load.Nb, 4 * load.Nb));
            }
            // Anchor-preferred ownership, applied before ANY consumer reads owner or scatter — the part
            // scatter maps, the owner buffer, the tier scatter and the witness reservations all see one
            // verdict. Needs the anchor's conditioning, which is why it waits for the operator loop.
            int movedRows = union.Owner.Count(o => o != anchorIdx);
            union = PoolMath.PreferAnchorOwnership(union, anchorIdx, partArts[anchorIdx].Weak);
            movedRows -= union.Owner.Count(o => o != anchorIdx);
            if (movedRows > 0)
                diagnostics.Add($"{sfx}: {movedRows} union bone{(movedRows == 1 ? "" : "s")} re-owned to the "
                    + "anchor — recovered at its own draw instead of another part's");
            for (int i = 0; i < parts.Count; i++)
                partScatter.Add((uint[])union.ScatterMaps[i].Clone());
            File.WriteAllBytes(Path.Combine(req.OutDir, $"owner_part_{sfx}.buf"),
                UIntBytes(union.Owner.Select(o => (uint)o).ToArray()));

            // ---- per-tier operators: same union and per-bone ownership as the part's lod0 -------------
            var tierMeta = new List<(string Part, string Name, string Suffix, string Hash, int Rows, DrawShapeSet? Shapes)>();
            var tierWork = new List<(string Name, int PartIdx, uint[] Scatter, OperatorArt Art)>();
            foreach (var t in pipe.Tiers ?? Array.Empty<PoolTier>())
            {
                int pi = parts.IndexOf(t.Part);
                if (pi < 0) throw new InvalidOperationException($"{sfx}: tier '{t.Name}': '{t.Part}' is not a pool part");
                ClaimName(t.Name, t.DumpDir);
                var load = Load(t.DumpDir);
                // both sides restated in the pipeline's reference space, so the gate below reads a real
                // bind difference rather than the tier's own authoring space
                var tierIn = UnionInput(t.DumpDir);
                var (tierHashes, tierBinds) = (tierIn.Hashes, tierIn.Binds);
                var scatter = new uint[load.Nb];
                // the whole tier's per-bone weight in one traversal, on the first bone that needs it
                double[]? tierWeight = null;
                for (int b = 0; b < load.Nb; b++)
                {
                    int u = Array.IndexOf(union.UnionHashes, tierHashes[b]);
                    if (u < 0)
                    {
                        // A weightless bone poses no vertices, so the palette owes it no row: the sentinel
                        // is the recover shader's "write nothing". A WEIGHTED bone off the union is the real
                        // fault — the tier's geometry rides it and nothing can pose it.
                        if ((tierWeight ??= SummedWeights(load))[b] > 0)
                            throw new InvalidOperationException(
                                $"tier '{t.Name}' rigs bone 0x{tierHashes[b]:x8} that no pool part's lod0 carries — "
                                + "the union palette can't pose it");
                        scatter[b] = PoolMath.Sentinel;
                        continue;
                    }
                    double d0 = 0;
                    if (unionInputs[pi].Binds.TryGetValue(tierHashes[b], out var lodBind))
                        for (int m = 0; m < 16; m++) d0 = Math.Max(d0, Math.Abs(lodBind[m] - tierBinds[tierHashes[b]][m]));
                    if (d0 > BindSpace.MaxBindDisagreement)
                        throw new InvalidOperationException(
                            $"tier '{t.Name}' bone 0x{tierHashes[b]:x8} has a different bind pose than the part's lod0 "
                            + $"(max diff {d0:g4}); the difference isn't one rigid rotation, so the tier can't be "
                            + "converted into the part's space");
                    scatter[b] = union.Owner[u] == pi ? (uint)u : PoolMath.Sentinel;
                }
                // a decimated tier can leave an owned bone with degenerate weighted-vertex support — its
                // rows are tied to a sound co-riding bone (see BuildOperator). Only a bone with NO sound
                // stand-in falls back to the sentinel, keeping its lod0-recovered row — which lives in the
                // lod0 draw's space, so a same-frame two-placement context displaces it.
                var art = Operator(t.Name, t.DumpDir);
                for (int b = 0; b < load.Nb; b++)
                    if (scatter[b] != PoolMath.Sentinel && art.Weak[b] && art.Tie[b] < 0)
                    {
                        scatter[b] = PoolMath.Sentinel;
                        diagnostics.Add($"{t.Name}: bone 0x{tierHashes[b]:x8} is too weakly supported in this tier — "
                            + "its lod0 recovery is reused for draws at this tier");
                    }
                // Anchor-preferred ownership widens what the ANCHOR's tiers are asked to serve; a bone
                // the preference took on the lod0 verdict that this tier doesn't carry at all is served
                // by nobody in this tier's chain — its lod0 row simply stands. Named so the class is
                // visible if a decimated anchor tier ever drops a bone another part poses live.
                if (pi == anchorIdx)
                {
                    var tierSet = new HashSet<uint>(tierHashes);
                    for (int u2 = 0; u2 < union.UnionHashes.Length; u2++)
                        if (union.Owner[u2] == anchorIdx && !tierSet.Contains(union.UnionHashes[u2]))
                            diagnostics.Add($"{t.Name}: this anchor tier does not carry bone "
                                + $"0x{union.UnionHashes[u2]:x8} — its lod0 row stands at this tier's draws");
                }
                tierWork.Add((t.Name, pi, scatter, art));
                tierMeta.Add((t.Part, t.Name, t.Suffix, t.CaptureHash, 4 * load.Nb, t.Shapes));
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
                if (pi == anchorIdx) continue;
                var partOps = new List<(uint[] Scatter, OperatorArt Art)> { (partScatter[pi], partArts[pi]) };
                partOps.AddRange(tierWork.Where(t => t.PartIdx == pi).Select(t => (t.Scatter, t.Art)));

                uint witness = 0;
                bool found = false;
                foreach (var h in partArts[anchorIdx].Hashes)
                    if (partOps.All(o => Sound(o.Art, h)) && anchorOps.All(o => Sound(o.Art, h)))
                    { witness = h; found = true; break; }
                if (!found)
                {
                    diagnostics.Add($"{sfx}: {parts[pi]} shares no sound bone with the anchor — its owned bones "
                        + "have no current-frame geometry conversion");
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

            var lod0Owners = union.Owner.Where(pi => pi != anchorIdx).Distinct().ToList();
            bool lod0WitnessConvert = lod0Owners.All(pi => witRows[pi].PartRow != uint.MaxValue);
            if (!lod0WitnessConvert)
                diagnostics.Add($"{sfx}: LOD0 has no complete current-frame witness conversion — it falls "
                    + "back to per-draw constants, whose freshness depends on draw order");
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
            var pipeGroups = pipe.Groups ?? Array.Empty<PoolGroup>();
            uint groupBase = nextSlot;
            int groupBoneCount = pipeGroups.Sum(g => g.GroupBones.Count);
            // The whole region is handed out BEFORE any member work, so it stays contiguous: a member's
            // witness reservation below takes a slot past it, and a region interleaved with those would put
            // the donor's compiled indices on the wrong rows.
            nextSlot += (uint)groupBoneCount;
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
                        groupClaims.Add(new GroupMemberClaim(m.Name, m.CaptureHash, member.Hidden));
                    var lod0 = meshes.FirstOrDefault(m => m.IsLod0);
                    if (lod0 is null)
                    {
                        diagnostics.Add($"{sfx}: group member '{member.Mesh}' carries no lod0 draw. It "
                            + "writes no rows, and the group's other members cover the bones while they are "
                            + "on screen");
                        continue;
                    }
                    var tiers = meshes.Where(m => !m.IsLod0).ToList();

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
                    var lod0Art = Operator(lod0.Name, lod0.DumpDir);
                    uint lod0Witness = 0, lod0WitnessRow = 0;
                    bool lod0HasWitness = false;
                    foreach (var h in partArts[anchorIdx].Hashes)
                        if (Sound(lod0Art, h) && anchorOps.All(o => Sound(o.Art, h)))
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
                            if (tierArts.All(a => Sound(a, h)) && anchorOps.All(o => Sound(o.Art, h)))
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
                                diagnostics.Add($"{sfx}: {mesh.Name} does not carry bone 0x{g.GroupBones[k]:x8}, "
                                    + "so it writes no rows for it");
                            }
                            else if (art.Weak[idx])
                            {
                                gmap[k] = PoolMath.Sentinel;
                                diagnostics.Add($"{sfx}: {mesh.Name} recovers bone 0x{g.GroupBones[k]:x8} "
                                    + "ill-conditioned, so it writes no rows for it");
                            }
                            else gmap[k] = (uint)idx;
                        }
                        if (gmap.All(v => v == PoolMath.Sentinel)) continue;   // nothing left for it to write
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

            for (int i = 0; i < parts.Count; i++)
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

            File.WriteAllText(Path.Combine(req.OutDir, $"convert_cs_{sfx}.hlsl"),
                ComputeTemplates.EmitConvert(parts.Count, ub));

            // ---- the new geometry: donor streams, or the identity concat of the pool parts -----------
            int vcount, vb1Stride;
            List<PoolMath.Submesh> submeshes;
            string ibFmt;
            // Identity builds carry no ties — NOT because absence is harmless there (the identity body
            // concatenates every part, so an absent part's region is on screen and its rows collapse the
            // same way), but because the route ships from no app build (ModBuilder always sets DonorDir)
            // and carries no bone paths to tie with. Emitter-API/test reach only; recorded, not cured.
            var ties = new List<(string Part, int Pairs)>();
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
                if (groupBoneCount > 0) ShiftGroupIndices(skinStream, ub, groupBase, groupBoneCount);
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
                int donorBones = ub + groupBoneCount;
                if (newBi.Length > 0 && maxBi >= donorBones)
                    warnings.Add(groupBoneCount == 0
                        ? $"{sfx}: new geometry references union bone {maxBi} but the union has {ub} (0..{ub - 1}). " +
                          "Recompile the donor against THIS union."
                        : $"{sfx}: new geometry references bone {maxBi} but the union and its wardrobe slots have " +
                          $"{donorBones} (0..{donorBones - 1}). Recompile the donor against THIS union.");

                // ---- tie underlay: a donor-used union bone another part owns rides its nearest
                // anchor-owned ancestor while that part's presence latch is down. Weighted use only — a
                // slot the donor merely indexes at zero weight moves no vertex and earns no tie.
                var donorSlots = new HashSet<int>();
                for (int i = 0; i < newBi.GetLength(0); i++)
                    for (int k = 0; k < 4; k++)
                        if (newW[i, k] > 0 && newBi[i, k] < ub) donorSlots.Add(newBi[i, k]);
                ties = TieUnderlay(req.OutDir, sfx, union, anchorIdx, parts, donorSlots,
                    pipe.BonePaths, diagnostics);
            }
            vcountTotal += vcount;

            File.WriteAllText(Path.Combine(req.OutDir, $"skin_cs_{sfx}.hlsl"), ComputeTemplates.EmitSkin(vcount));

            // union bone order (the donor-compile contract)
            File.WriteAllText(Path.Combine(req.OutDir, $"union_{sfx}.json"),
                UnionJson(ub, union.UnionHashes, partMeta));

            int bpi = ibFmt.Contains("R16") ? 2 : 4;
            var draws = submeshes.Select(s => (s.IndexCount, Start: s.FirstByte / bpi, s.BaseVertex)).ToList();

            // ---- per-submesh maps ---------------------------------------------------------------------
            var subMaps = SubMapsFor(sfx, subTexOverrides, draws.Count);

            pipes.Add(new PipelineEmission(sfx, partMeta, anchorIdx, capHashes, ub, vcount, vb1Stride,
                ibFmt, draws, subMaps,
                pipe.NoSkipParts is { Count: > 0 } ns ? new HashSet<string>(ns, StringComparer.Ordinal) : null,
                tierMeta, lod0WitnessConvert, pipe.ToggleKey, pipe.Latch, pipe.HideWhenOff, groupSections, groupClaims, ties,
                pipe.PresenceHashes, pipe.AnchorShapes));
        }

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

                rigids.Add(new RigidEmission(sfx, r.Hashes.ToList(),
                    streams.FirstOrDefault(s => s.Stream == 0).Stride,
                    hasVb1 ? streams.FirstOrDefault(s => s.Stream == 1).Stride : null,
                    ibFmt, draws, subMaps, r.ToggleKey, r.Latch, r.HideWhenOff, r.ShapesByHash));
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
        // ---- the ini --------------------------------------------------------------------------------
        // Section ownership is settled before a byte is written: one ib hash owns exactly one
        // TextureOverride, so sighting assignments and scoped binds land INSIDE the owning section. The
        // retexture text is composed first — it hands its same-hash bind blocks to the capture units —
        // and appended after the pooled emission.
        // one guard per guarded hash settles here, so the tag sections, the declarations, the collision
        // walk and the emission all read the same dictionary
        var guards = GuardsByHash(req.TwinGuards);
        RefuseTagCollisions(slotTags.Select(t => (Hash: t.Hash, Part: t.Part)),
            (req.ScopedRetextures ?? Array.Empty<ScopedRetexEntry>())
                .Select(e => (Hash: e.StockHash, Part: e.Part)),
            MintedTwinTagHashes(guards.Values).Select(h => (Hash: h, Part: "")));
        var hides = (req.HideHashes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        var units = BuildCaptureUnits(pipes, req.ToggleKey, guards);
        foreach (var h in hides)
            if (units.ByHash.ContainsKey(h))
                throw new InvalidOperationException(
                    $"hide hash {h} is also a pipeline capture hash — the capture's skip already covers it");
        // A rigid replacement owns a section per hash it draws at, so no other section may claim one: two
        // on a hash leave the second dropped at parse time, and which one survived could not be predicted.
        var hideSet = new HashSet<string>(hides, StringComparer.Ordinal);
        var rigidOwner = new Dictionary<string, RigidEmission>(StringComparer.Ordinal);
        foreach (var r in rigids)
            foreach (var h in r.Hashes)
            {
                if (units.ByHash.ContainsKey(h))
                    throw new InvalidOperationException(
                        $"'{r.Sfx}' replaces draw {h}, which a pooled pipeline also captures. "
                        + "The two can't share one section, so this build can't ship");
                if (hideSet.Contains(h))
                    throw new InvalidOperationException(
                        $"'{r.Sfx}' replaces draw {h}, which is also hidden. "
                        + "The replacement's own suppression already covers it");
                if (rigidOwner.TryGetValue(h, out var owner))
                    throw new InvalidOperationException(
                        $"'{owner.Sfx}' and '{r.Sfx}' replace one draw signature. The swap can't tell them "
                        + "apart, so this build can't ship");
                rigidOwner[h] = r;
            }
        RefuseHiddenScopedAnchors(hides, req.ScopedRetextures);
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
                string anchor = p.PartMeta[p.AnchorIdx].Part;
                return p.GroupMembers.Where(m => !m.AtDraw).Select(m => (m.Name, m.Hash))
                    .Concat(p.PartMeta
                        .Where(pm => !string.Equals(pm.Part, anchor, StringComparison.Ordinal))
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
            LiveSightings(req.TwinSightings, guards.Values));
        string retexIni = req.Retextures is { Count: > 0 } || req.ScopedRetextures is { Count: > 0 }
                          || guards.Count > 0
            ? RetexIni(req.Retextures ?? Array.Empty<RetexEntry>(), req.OutDir, req.ToggleKey,
                req.ScopedRetextures, units, sightings, rigidOwner, guards.Values, tagKinds)
            : "";
        string ini = EmitIni(pipes, rigids, units, hides, sightings, slotTags, slimParts,
            req.ToggleKey, req.HideKeys, req.Retextures ?? Array.Empty<RetexEntry>(),
            req.ScopedRetextures ?? Array.Empty<ScopedRetexEntry>(), allLatches, req.HideLatches,
            req.KeysStartingOff, guards) + retexIni;
        File.WriteAllText(Path.Combine(req.OutDir, "mod.ini"), ini);

        return new Result(req.OutDir, ubTotal, vcountTotal, warnings, diagnostics);
    }

    /// <summary>A mod with no Replace verbs — retextures and/or hides only. No compute pipeline, geometry,
    /// neutral maps or pass flags: a hide keys on the mesh's index buffer and a retexture on the stock
    /// texture's own hash, so neither needs to know which pass is drawing. Throws when BOTH lists are
    /// empty.</summary>
    public Result BuildOverlaysOnly(string outDir, IReadOnlyList<RetexEntry>? entries,
        IReadOnlyList<string>? hideHashes = null, string? modKey = null,
        IReadOnlyDictionary<string, string>? hideKeys = null,
        IReadOnlyList<ScopedRetexEntry>? scopedEntries = null,
        IReadOnlyList<WitnessLatch>? latches = null,
        IReadOnlyDictionary<string, string>? hideLatches = null,
        IReadOnlyCollection<string>? keysStartingOff = null,
        IReadOnlyList<TwinGuard>? twinGuards = null,
        IReadOnlyList<TwinSighting>? twinSightings = null)
    {
        var retex = entries ?? Array.Empty<RetexEntry>();
        var scoped = scopedEntries ?? Array.Empty<ScopedRetexEntry>();
        var guards = GuardsByHash(twinGuards);
        // deduped for the same reason the pooled path dedupes: one hash owns one TextureOverride, and a
        // second section on it would be dropped at parse time
        var hides = (hideHashes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        if (retex.Count == 0 && scoped.Count == 0 && hides.Count == 0)
            throw new InvalidOperationException("overlay-only build with no retextures and no hides");
        RefuseHiddenScopedAnchors(hides, scoped);
        // No pipelines here, so nothing slot-tags an anchor's stock maps: every hash is a retexture's or
        // a twin guard's.
        RefuseTagCollisions(Array.Empty<(string, string)>(),
            scoped.Select(e => (Hash: e.StockHash, Part: e.Part)),
            MintedTwinTagHashes(guards.Values).Select(h => (Hash: h, Part: "")));
        Directory.CreateDirectory(outDir);
        // no pipelines here, so no hash is capture-claimed; a sighting still routes into the hide or
        // scoped-anchor section that owns its ib
        var units = BuildCaptureUnits(Array.Empty<PipelineEmission>(), modKey, guards);
        var sightings = RouteSightings(latches, units, hides, scoped,
            twins: LiveSightings(twinSightings, guards.Values));
        var P = new StringBuilder();
        P.Append("; Overlay overrides - generated by the Remold overlay emitter\n"
               + "; hide skips every pass of a mesh; retexture rebinds a stock texture by its own\n"
               + "; resource hash, which covers every pass, environment and LOD it is sampled in.\n\n");
        // an overlay-only mod has no [Constants] of its own, so a keyed or latched one declares its
        // variables here or every gate would test an undefined name
        var overlayKeys = hides.Select(h => HideKey(hideKeys, h)).Concat(retex.Select(r => r.ToggleKey))
            .Concat(scoped.SelectMany(r => r.Images).Select(i => i.ToggleKey)).ToList();
        var declared = ModKeys.Distinct(new[] { modKey }.Concat(overlayKeys));
        var lat = latches ?? Array.Empty<WitnessLatch>();
        if (declared.Count > 0 || lat.Count > 0 || scoped.Count > 0 || guards.Count > 0)
        {
            P.Append("[Constants]\n");
            if (scoped.Count > 0) P.Append($"global ${VarRetexProbe} = 0\nglobal ${VarRetexSlot} = 0\n");
            if (guards.Count > 0)
            {
                // the slot probe belongs to the guards that carry tags; a build whose verdicts all arrive
                // from sightings never reads a slot
                if (guards.Values.Any(g => g.Tags.Count > 0)) P.Append($"global ${VarProbe} = 0\n");
                foreach (var v in TwinVars(guards.Values)) P.Append($"global ${v} = 0\n");
                // the multi-verdict guards' scratch, rewritten at every guard it opens rather than carried
                if (TwinScratchNeeded(guards.Values)) P.Append($"global ${VarTwinOk} = 0\n");
            }
            foreach (var l in lat)
                P.Append($"global ${GateVar(l.Name)} = 0\nglobal ${SeenVar(l.Name)} = 0\n");
            P.Append(KeyDeclarations(declared, keysStartingOff));
            P.Append("\n");
        }
        if (lat.Count > 0)
        {
            P.Append("[Present]\n");
            foreach (var l in lat)
                P.Append($"${GateVar(l.Name)} = ${SeenVar(l.Name)}\n${SeenVar(l.Name)} = 0\n");
            P.Append("\n");
        }
        P.Append(KeysIni(modKey, overlayKeys));
        P.Append(WitnessIni(sightings));
        for (int i = 0; i < hides.Count; i++)
        {
            OpenTextureOverride(P, $"Hide_{i}", hides[i]);
            // sighting UNGATED: a witness silenced while a key was off would read the outfit as
            // absent the frame it comes back on
            if (sightings.ByHash.TryGetValue(hides[i], out var seen))
                foreach (var line in seen) P.Append(line).Append('\n');
            // this hash also fires on a sibling mesh's draws, so the skip waits for the probe to find
            // the hidden mesh's own tagged texture bound
            bool hideGuarded = OpenTwinGuardIfAny(P, guards, hides[i]);
            var gate = new Gate(new[] { modKey, HideKey(hideKeys, hides[i]) },
                HideKey(hideLatches, hides[i]) is { } hl ? new[] { GateVar(hl) } : null);
            gate.Open(P);
            P.Append("handling = skip\n");
            gate.Close(P);
            CloseTwinGuard(P, hideGuarded);
            P.Append("\n");
        }
        if (retex.Count > 0 || scoped.Count > 0 || guards.Count > 0)
            P.Append(RetexIni(retex, outDir, modKey, scoped, units, sightings, null, guards.Values));
        File.WriteAllText(Path.Combine(outDir, "mod.ini"), P.ToString());
        return new Result(outDir, 0, 0, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>The per-hide key for one hash, or null when that hide carries none.</summary>
    static string? HideKey(IReadOnlyDictionary<string, string>? keys, string hash) =>
        keys is not null && keys.TryGetValue(hash, out var k) ? k : null;

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
    /// replaced mesh's vanilla shape set, the draw's gate, and the donor's own draw count. Donor range k
    /// belongs to vanilla submesh k; ranges past the last vanilla submesh join it.</summary>
    sealed record RoutedDraw(string Sfx, DrawShapeSet Shapes, Gate DrawGate, int DonorDraws,
        bool IsRigid = false);

    /// <summary>The build's capture sections, in emission order and by the hash each one owns.</summary>
    sealed record CaptureUnits(List<CaptureUnit> Ordered, Dictionary<string, CaptureUnit> ByHash);

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
        IReadOnlyDictionary<string, TwinGuard> guards)
    {
        // A hash routes its donor draw per submesh only when the replaced mesh really has several
        // DRAWABLE submeshes (a zero-index-count submesh is a material slot with no geometry — the game
        // issues no draw for it) AND the hash carries no twin guard: a guarded section's draw must stay
        // inside the guard's verdict, so a guarded multi-submesh target keeps the draw in its capture
        // section (every range at every fire).
        DrawShapeSet? RoutedShapes(DrawShapeSet? shapes, string hash)
            => shapes is not null && shapes.Shapes.Count(sh => sh.Count > 0) > 1
                && !guards.ContainsKey(hash) ? shapes : null;
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
            var latchVars = pipe.Latch is null ? null : new[] { GateVar(pipe.Latch) };
            var drawGate = new Gate(new[] { modKey, pipe.ToggleKey }, latchVars);
            var skipGate = pipe.HideWhenOff ? new Gate(new[] { modKey }, latchVars) : drawGate;
            for (int idx = 0; idx < pipe.PartMeta.Count; idx++)
            {
                var part = pipe.PartMeta[idx].Part;
                string h = pipe.CapHashes.TryGetValue(part, out var cv) ? cv : $"REPLACE_{part}_ib";
                var u = Unit(h, $"Cap_{part}");
                u.Capture($"Resource_{part}_Posed = ref vb0");
                u.Capture($"Resource_{part}_CB = copy vs-cb1");
                // The sticky flag an AT-DRAW member lod0 waits on, set right where the anchor's constants
                // land: this is the ONE capture that fills the CB its rebase reads. Never reset, so it
                // stays 1 for the rest of the session once the anchor has drawn once. In-chain members
                // need no flag — the chain itself runs at the anchor's draw.
                if (idx == pipe.AnchorIdx && pipe.GroupMembers.Any(m => m.AtDraw))
                    u.Capture($"${GroupCbVar(sfx)} = 1");
                if (pipe.NoSkip?.Contains(part) != true) u.Suppress(skipGate);
                if (idx == pipe.AnchorIdx)
                {
                    // recover/convert/skin once per frame (the flag resets in [Present]); the DRAW runs at
                    // every fire — suppressing draws kills shadows/outlines
                    var chain = new List<string>
                    {
                        $"if $zz_done_{sfx} == 0",
                    };
                    for (int pi = 0; pi < pipe.PartMeta.Count; pi++)
                        RecoverRun(chain, pipe, pi, pipe.PartMeta[pi].Part, sfx);
                    chain.Add($"run = CustomShaderConvert{(pipe.Lod0WitnessConvert ? "W" : "")}_{sfx}");
                    MemberRuns(chain, pipe, sfx);
                    TieRuns(chain, pipe, sfx);
                    chain.Add($"run = CustomShaderSkin_{sfx}");
                    chain.Add($"$zz_done_{sfx} = 1");
                    chain.Add("endif");
                    if (RoutedShapes(pipe.AnchorShapes, h) is { } routed0)
                        u.RoutedDraws.Add(new RoutedDraw(sfx, routed0, drawGate, pipe.Draws.Count));
                    else
                        chain.Add($"run = CommandListDraw_{sfx}");
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
                u.Capture($"Resource_{t.Name}_Posed = ref vb0");
                if (pipe.NoSkip?.Contains(t.Part) != true) u.Suppress(skipGate);
                if (t.Part == anchor)
                {
                    var chain = new List<string> { $"if $zz_done_{sfx}_{t.Suffix} == 0" };
                    for (int pi = 0; pi < pipe.PartMeta.Count; pi++)
                    {
                        string p2 = pipe.PartMeta[pi].Part;
                        var pt = pipe.TierMeta.FirstOrDefault(x => x.Part == p2 && x.Suffix == t.Suffix);
                        RecoverRun(chain, pipe, pi, pt.Name ?? p2, sfx);
                    }
                    chain.Add($"run = CustomShaderConvertW_{sfx}");
                    MemberRuns(chain, pipe, sfx);
                    TieRuns(chain, pipe, sfx);
                    chain.Add($"run = CustomShaderSkin_{sfx}");
                    chain.Add($"$zz_done_{sfx}_{t.Suffix} = 1");
                    chain.Add("endif");
                    if (RoutedShapes(t.Shapes, t.Hash) is { } routedT)
                        u.RoutedDraws.Add(new RoutedDraw(sfx, routedT, drawGate, pipe.Draws.Count));
                    else
                        chain.Add($"run = CommandListDraw_{sfx}");
                    foreach (var line in drawGate.Wrap(chain)) u.Run(line);
                }
            }

            // wardrobe-group members: the member's own draw captures its posed vertices and latches its
            // presence; the fused dispatch runs in the anchor's chains, gated on LAST frame's latch. The
            // section is the one this hash already owns where another pipeline pools the same mesh — two
            // sections on one hash leave the second dropped at parse time — and the member NEVER adds a
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
            foreach (var c in pipe.GroupClaims)
                if (c.Hidden) Unit(c.Hash, $"Cap_{c.Name}").Suppress(skipGate);
        }
        return new CaptureUnits(ordered, byHash);
    }

    /// <summary>Where each presence latch's sighting assignment lands. A witness ib whose hash already owns
    /// a TextureOverride records the sighting INSIDE that section; only an unclaimed hash mints a
    /// <c>[TextureOverride_Witness_*]</c> of its own. Two sections on one hash would leave the second
    /// dropped at parse time, so which one survives could not be predicted.</summary>
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
        IReadOnlyList<TwinSighting>? twins = null)
    {
        var s = new Sightings();
        var hideSet = new HashSet<string>(hides, StringComparer.Ordinal);
        hideSet.UnionWith(rigidHashes ?? Array.Empty<string>());
        var anchors = new HashSet<string>((scoped ?? Array.Empty<ScopedRetexEntry>())
            .SelectMany(e => e.Images).SelectMany(i => i.Anchors).Select(a => a.Hash), StringComparer.Ordinal);
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
    static void RefuseTagCollisions(IEnumerable<(string Hash, string Part)> slotTags,
        IEnumerable<(string Hash, string Part)> retexes,
        IEnumerable<(string Hash, string Part)>? twinTags = null)
    {
        // enumerated in arrival order, so a refusal reads the same way twice
        var retexInOrder = retexes.ToList();
        var retex = new HashSet<string>(retexInOrder.Select(r => r.Hash), StringComparer.OrdinalIgnoreCase);
        var byTag = new Dictionary<int, (string Hash, string Part)>();
        // deduped by HASH alone: one texture reaching the build twice is one texture
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var one in slotTags.Concat(retexInOrder)
                     .Concat(twinTags ?? Array.Empty<(string, string)>()))
        {
            if (!seen.Add(one.Hash)) continue;
            int tag = RetexTag(one.Hash);
            if (byTag.TryGetValue(tag, out var other))
                throw new InvalidOperationException(
                    $"Stock textures {Named(other)} and {Named(one)} derive the same slot tag ({tag}). "
                    + "The draw probes can't tell the two apart. "
                    + TagCollisionFix(retex.Contains(other.Hash), other.Part, retex.Contains(one.Hash), one.Part));
            byTag[tag] = one;
        }

        static string Named((string Hash, string Part) t) =>
            t.Part.Length > 0 ? $"{t.Hash} on {t.Part}" : t.Hash;
    }

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
        string? ToggleKey, string? Latch, bool HideWhenOff,
        IReadOnlyDictionary<string, DrawShapeSet>? ShapesByHash)
    {
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
    /// owning section's capture/compute always precedes these draws. A vanilla shape no section names
    /// draws nothing — its original is already suppressed — and a full shape colliding with a submesh
    /// shape yields that submesh's section alone (the two draws cannot be told apart).</summary>
    static void EmitRoutedDrawSections(StringBuilder P, string hash, string ownerName,
        IReadOnlyList<RoutedDraw> routedDraws)
    {
        // Every entry describes the one mesh this hash names, so their shape sets agree; the first
        // states them. Two submeshes covering one index range are one draw the runtime cannot tell
        // apart, so DISTINCT SHAPES — not submesh indices — get sections, and a zero-index-count
        // submesh (a material slot with no geometry) never draws: donor ranges folding onto one land
        // on the last drawable shape instead.
        var shapes = routedDraws[0].Shapes.Shapes;
        int lastDrawable = -1;
        for (int k = 0; k < shapes.Count; k++) if (shapes[k].Count > 0) lastDrawable = k;
        var groups = new List<(DrawShape Shape, int FirstK, List<int> Ks)>();
        for (int k = 0; k < shapes.Count; k++)
        {
            if (shapes[k].Count == 0) continue;
            int gi = groups.FindIndex(g => g.Shape == shapes[k]);
            if (gi < 0) groups.Add((shapes[k], k, new List<int> { k }));
            else groups[gi].Ks.Add(k);
        }
        // a donor range's target submesh, zero-count folds retargeted to the last drawable shape
        int TargetK(int di)
        {
            int t = Math.Min(di, shapes.Count - 1);
            return shapes[t].Count == 0 ? lastDrawable : t;
        }
        var emitted = new List<DrawShape>();
        foreach (var (shape, firstK, ks) in groups)
        {
            var runs = routedDraws
                .Select(r => (Routed: r, Dis: Enumerable.Range(0, r.DonorDraws)
                    .Where(di => ks.Contains(TargetK(di))).ToList()))
                .Where(x => x.Dis.Count > 0).ToList();
            if (runs.Count == 0) continue;
            OpenTextureOverride(P, $"{ownerName}_DrawS{firstK}", hash);
            P.Append($"match_first_index = {shape.First}\n");
            P.Append($"match_index_count = {shape.Count}\n");
            foreach (var (routed, dis) in runs)
            {
                string listStem = routed.IsRigid ? "CommandListRigid" : "CommandListDraw";
                routed.DrawGate.Open(P);
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
        OpenTextureOverride(P, $"{ownerName}_DrawFull", hash);
        P.Append("match_first_index = 0\n");
        P.Append($"match_index_count = {full}\n");
        foreach (var routed in routedDraws)
        {
            routed.DrawGate.Open(P);
            P.Append($"run = {(routed.IsRigid ? "CommandListRigid" : "CommandListDraw")}_{routed.Sfx}\n");
            routed.DrawGate.Close(P);
        }
        P.Append("\n");
    }

    static void AppendTwinProbe(StringBuilder P, TwinGuard guard)
    {
        if (guard.Tags.Count == 0) return;
        foreach (int s in ProbeSlots)
        {
            P.Append($"${VarProbe} = ps-t{s}\n");
            foreach (var t in guard.Tags)
                P.Append($"if ${VarProbe} == {t.TagValue}\n${guard.Var} = {t.Verdict}\nendif\n");
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
    static bool OpenTwinGuardIfAny(StringBuilder P, IReadOnlyDictionary<string, TwinGuard> guards,
        string hash)
    {
        if (!guards.TryGetValue(hash, out var guard)) return false;
        AppendTwinProbe(P, guard);
        OpenTwinGuard(P, guard);
        return true;
    }

    static void CloseTwinGuard(StringBuilder P, bool opened) { if (opened) P.Append("endif\n"); }

    /// <summary>Whether any emitted guard admits more than one verdict, so the build declares
    /// <see cref="VarTwinOk"/>. False leaves the declarations exactly where a single-verdict build has
    /// them.</summary>
    static bool TwinScratchNeeded(IEnumerable<TwinGuard> guards) =>
        guards.Any(g => g.OwnVerdicts.Count > 1);

    /// <summary>Every sticky variable the emitted guards read, first-seen order. Declared in
    /// <c>[Constants]</c> and written nowhere else, so an unidentified signature reads 0 and the
    /// sections it guards stay inert.</summary>
    static List<string> TwinVars(IEnumerable<TwinGuard> guards) =>
        guards.Select(g => g.Var).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The stock textures a guard probe needs a section of its own on: the ones whose tag value
    /// is derived from the hash rather than carried by a slot tag the build already emits.</summary>
    static List<string> MintedTwinTagHashes(IEnumerable<TwinGuard> guards) =>
        guards.SelectMany(g => g.Tags)
            .Where(t => t.TagValue == RetexTag(t.TexHash))
            .Select(t => t.TexHash)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    string EmitIni(List<PipelineEmission> pipes, List<RigidEmission> rigids, CaptureUnits units,
        IReadOnlyList<string> hideHashes,
        Sightings sightings, IReadOnlyList<StockMapTag> slotTags,
        IReadOnlySet<string> slimParts, string? modKey, IReadOnlyDictionary<string, string>? hideKeys,
        IReadOnlyList<RetexEntry> retextures, IReadOnlyList<ScopedRetexEntry>? scopedRetextures = null,
        IReadOnlyList<WitnessLatch>? latches = null, IReadOnlyDictionary<string, string>? hideLatches = null,
        IReadOnlyCollection<string>? keysStartingOff = null,
        IReadOnlyDictionary<string, TwinGuard>? twinGuards = null)
    {
        var P = new StringBuilder();
        var guards = twinGuards ?? new Dictionary<string, TwinGuard>(StringComparer.Ordinal);

        // Emitted ini header — ships in every generated mod, so it describes the mod, not this code;
        // each route describes only itself.
        if (pipes.Count > 0)
            P.Append("; Pooled mesh swap - generated by the Remold pooled mesh-swap emitter\n"
                   + "; one pipeline per replacement: capture each pool part's posed vb0 + vs-cb1 -> recover\n"
                   + "; into that pipeline's union palette (rows in each owner part's draw space) -> CONVERT\n"
                   + "; all rows into the anchor's space at the anchor draw -> skin the new geometry once ->\n"
                   + "; draw at the anchor (in EVERY pass the anchor fires in; texture binds probe the\n"
                   + "; slots actually bound at the draw, via filter_index tags on the anchor's own stock\n"
                   + "; maps) -> hide the other meshes. Meshes pooled by several\n"
                   + "; pipelines are captured once; their capture section serves every pipeline.\n"
                   + "; Compute (recover/convert/skin) runs ONCE per frame per chain ($zz_done_* flags,\n"
                   + "; reset in [Present]); the draw runs at every pass fire.\n\n");
        if (rigids.Count > 0)
            P.Append("; Rigid mesh swap - generated by the Remold rigid mesh-swap emitter\n"
                   + "; one section per replaced draw: skip the vanilla draw and issue the new geometry in\n"
                   + "; its place, at every shipped LOD tier. The draw is not posed per vertex, so nothing\n"
                   + "; is captured or recovered; texture binds probe the slots actually bound at the draw,\n"
                   + "; via filter_index tags on the part's own stock maps.\n\n");

        // per-frame compute flags: one per pipeline's lod0 chain, one per anchored tier chain
        var doneFlags = new List<string>();
        foreach (var pipe in pipes)
        {
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
            .Select(p => GroupCbVar(p.Sfx)).ToList();
        P.Append(FlagsIni(doneFlags, slotTags, modKey,
            pipes.Select(x => x.ToggleKey)
                .Concat(rigids.Select(x => x.ToggleKey))
                .Concat(hideHashes.Select(h => HideKey(hideKeys, h)))
                .Concat(retextures.Select(r => r.ToggleKey))
                .Concat((scopedRetextures ?? Array.Empty<ScopedRetexEntry>())
                    .SelectMany(r => r.Images).Select(i => i.ToggleKey)),
            latches, sightings, scopedRetextures is { Count: > 0 }, scopedHashes, keysStartingOff,
            TwinVars(guards.Values), TwinScratchNeeded(guards.Values),
            retextures.Select(r => r.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase), stickyFlags));

        // resource declarations: per-pipeline blocks; shared per-part resources declared by the first
        // pipeline that pools the part
        var declaredParts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pipe in pipes)
        {
            string sfx = pipe.Sfx;
            P.Append($"[Resource_Palette_{sfx}]\ntype = RWStructuredBuffer\nstride = 16\nfilename = palette_seed_{sfx}.buf\n");
            P.Append($"[Resource_PaletteConv_{sfx}]\ntype = RWStructuredBuffer\nstride = 16\nfilename = palette_seed_{sfx}.buf\n");
            P.Append($"[Resource_OwnerPart_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = owner_part_{sfx}.buf\n");
            foreach (var (part, _, _, _) in pipe.PartMeta)
            {
                if (declaredParts.Add(part))
                {
                    P.Append($"[Resource_{part}_Cpinv]\ntype = Buffer\nformat = DXGI_FORMAT_R32_FLOAT\nfilename = {part}_cpinv.buf\n");
                    if (slimParts.Contains(part))
                    {
                        P.Append($"[Resource_{part}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_sel.buf\n");
                        P.Append($"[Resource_{part}_Off]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_off.buf\n");
                    }
                    P.Append($"[Resource_{part}_Posed]\n\n");
                    P.Append($"[Resource_{part}_CB]\n\n");
                }
                P.Append($"[Resource_{part}_Map_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_map_{sfx}.buf\n");
            }
            foreach (var (_, name, _, _, _, _) in pipe.TierMeta)
            {
                if (declaredParts.Add(name))
                {
                    P.Append($"[Resource_{name}_Cpinv]\ntype = Buffer\nformat = DXGI_FORMAT_R32_FLOAT\nfilename = {name}_cpinv.buf\n");
                    if (slimParts.Contains(name))
                    {
                        P.Append($"[Resource_{name}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {name}_sel.buf\n");
                        P.Append($"[Resource_{name}_Off]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {name}_off.buf\n");
                    }
                    P.Append($"[Resource_{name}_Posed]\n\n");
                }
                P.Append($"[Resource_{name}_Map_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {name}_map_{sfx}.buf\n");
            }
            // wardrobe-group members: the same shared per-mesh declarations a pool part gets (a member this
            // build also pools is declared once), plus this pipeline's own group map
            foreach (var m in pipe.GroupMembers)
            {
                if (declaredParts.Add(m.Name))
                {
                    P.Append($"[Resource_{m.Name}_Cpinv]\ntype = Buffer\nformat = DXGI_FORMAT_R32_FLOAT\nfilename = {m.Name}_cpinv.buf\n");
                    if (slimParts.Contains(m.Name))
                    {
                        P.Append($"[Resource_{m.Name}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {m.Name}_sel.buf\n");
                        P.Append($"[Resource_{m.Name}_Off]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {m.Name}_off.buf\n");
                    }
                    // A member's LOD0 carries the same pair a pool part does — one mesh can be reached as a
                    // member here and pooled as a part by another pipeline under the same name, so
                    // whichever route declares it first must leave the other's binds something to name. A
                    // tier's name never meets a pool part's, and no tier binds constants.
                    P.Append($"[Resource_{m.Name}_Posed]\n\n");
                    if (m.Lod0) P.Append($"[Resource_{m.Name}_CB]\n\n");
                }
                P.Append($"[Resource_{m.Name}_GMap_{sfx}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {m.Name}_gmap_{sfx}.buf\n");
            }
            P.Append($"[Resource_NewBind_{sfx}]\ntype = RWBuffer\nstride = 40\nfilename = combined_bind_{sfx}.buf\n");
            P.Append($"[Resource_NewSkin_{sfx}]\ntype = RWBuffer\nstride = 32\nfilename = combined_skin_{sfx}.buf\n");
            P.Append($"[Resource_NewVB1_{sfx}]\ntype = RWBuffer\nstride = {pipe.Vb1Stride}\nfilename = combined_vb1_{sfx}.buf\n");
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
            P.Append($"[Resource_RigidIB_{r.Sfx}]\ntype = Buffer\nformat = {r.IbFmt}\n"
                   + $"filename = rigid_ib_{r.Sfx}.buf\n");
        }
        P.Append("[Resource_SaveVB0]\n\n[Resource_SaveVB1]\n\n[Resource_SaveVB3]\n\n[Resource_SaveIB]\n\n");
        // the save slots exist for the probe/bind range; a build with no donor textures never touches a
        // ps-t slot, so it declares none
        var subMapSets = pipes.Select(p => p.SubMaps).Concat(rigids.Select(r => r.SubMaps)).ToList();
        if (subMapSets.Any(DonorTexed))
        {
            foreach (int s in ProbeSlots) P.Append($"[Resource_SaveT{s}]\n\n");
            if (subMapSets.Any(m => UsesNeutral(m, StockMapKind.Normal)))
                P.Append("[Resource_NeutralN]\nfilename = neutral_n.dds\n");
            if (subMapSets.Any(m => UsesNeutral(m, StockMapKind.Rmo)))
                P.Append("[Resource_NeutralRMO]\nfilename = neutral_rmo.dds\n");
        }
        var texRes = new Dictionary<string, string>();
        foreach (var maps in subMapSets)
            foreach (var m in maps)
            foreach (var fn in new[] { m?.Albedo.File, m?.Normal.File, m?.Rmo.File })
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
                    foreach (var g in u.SkipGates)
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
            var latchVars = r.Latch is null ? null : new[] { GateVar(r.Latch) };
            var drawGate = new Gate(new[] { modKey, r.ToggleKey }, latchVars);
            var skipGate = r.HideWhenOff ? new Gate(new[] { modKey }, latchVars) : drawGate;
            // no key of its own: the two gates collapse into the single wrapped region of an unkeyed emission
            bool oneGate = string.Equals(skipGate.Id, drawGate.Id, StringComparison.Ordinal);
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
                bool rigidGuarded = OpenTwinGuardIfAny(P, guards, r.Hashes[i]);
                // a multi-submesh target routes its donor draw per submesh (the pooled anchors' rule);
                // a guarded hash keeps the draw here, inside the guard's verdict
                DrawShapeSet? routedShapes =
                    r.ShapesByHash?.GetValueOrDefault(r.Hashes[i]) is { } rs
                        && rs.Shapes.Count(sh => sh.Count > 0) > 1
                        && !guards.ContainsKey(r.Hashes[i]) ? rs : null;
                if (oneGate)
                {
                    drawGate.Open(P);
                    P.Append(routedShapes is null
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
                    if (routedShapes is null)
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
                if (routedShapes is null && hasScope)
                    foreach (var line in scope!) P.Append(line).Append('\n');
                P.Append("\n");
                if (routedShapes is not null)
                {
                    EmitRoutedDrawSections(P, r.Hashes[i], name,
                        new[] { new RoutedDraw(r.Sfx, routedShapes, drawGate, r.Draws.Count, IsRigid: true) });
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
                // this hash also fires on a sibling mesh's draws, so the skip waits for the probe to find
                // the hidden mesh's own tagged texture bound
                bool hideGuarded = OpenTwinGuardIfAny(P, guards, h);
                var hideGate = new Gate(new[] { modKey, HideKey(hideKeys, h) },
                    HideKey(hideLatches, h) is { } hl ? new[] { GateVar(hl) } : null);
                hideGate.Open(P);
                P.Append("handling = skip\n");
                hideGate.Close(P);
                CloseTwinGuard(P, hideGuarded);
                P.Append("\n");
            }
        }

        foreach (var pipe in pipes)
        {
            string sfx = pipe.Sfx;
            string anchor = pipe.PartMeta[pipe.AnchorIdx].Part;

            foreach (var (part, _, _, rows) in pipe.PartMeta)
                P.Append($"[CustomShaderRecover_{part}_{sfx}]\ncs = recover_{part}_cs.hlsl\n"
                       + $"cs-u1 = copy Resource_Palette_{sfx}\ncs-t0 = copy Resource_{part}_Posed\n"
                       + $"cs-t1 = Resource_{part}_Cpinv\ncs-t2 = Resource_{part}_Map_{sfx}\n"
                       + (slimParts.Contains(part) ? $"cs-t3 = Resource_{part}_Sel\ncs-t4 = Resource_{part}_Off\n" : "")
                       + $"Dispatch = {(rows + 63) / 64}, 1, 1\nResource_Palette_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            foreach (var (_, name, _, _, rows, _) in pipe.TierMeta)
                P.Append($"[CustomShaderRecover_{name}_{sfx}]\ncs = recover_{name}_cs.hlsl\n"
                       + $"cs-u1 = copy Resource_Palette_{sfx}\ncs-t0 = copy Resource_{name}_Posed\n"
                       + $"cs-t1 = Resource_{name}_Cpinv\ncs-t2 = Resource_{name}_Map_{sfx}\n"
                       + (slimParts.Contains(name) ? $"cs-t3 = Resource_{name}_Sel\ncs-t4 = Resource_{name}_Off\n" : "")
                       + $"Dispatch = {(rows + 63) / 64}, 1, 1\nResource_Palette_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

            P.Append($"[CustomShaderConvert_{sfx}]\ncs = convert_cs_{sfx}.hlsl\n"
                   + $"cs-u1 = copy Resource_PaletteConv_{sfx}\ncs-t0 = copy Resource_Palette_{sfx}\ncs-t1 = Resource_OwnerPart_{sfx}\n");
            for (int pi = 0; pi < pipe.PartMeta.Count; pi++)
                P.Append($"cs-cb{5 + pi} = Resource_{pipe.PartMeta[pi].Part}_CB\n");
            P.Append($"cs-cb13 = Resource_{anchor}_CB\n"
                   + $"Dispatch = {(4 * pipe.Ub + 63) / 64}, 1, 1\nResource_PaletteConv_{sfx} = copy cs-u1\npost cs-u1 = null\n\n");

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
                       + $"cs-t1 = Resource_{m.Name}_Cpinv\ncs-t2 = Resource_{m.Name}_GMap_{sfx}\n"
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

            // A COMMAND LIST, not a [CustomShader]: a CustomShader invocation unconditionally
            // saves/restores the viewports and the full OM state (RTVs+UAVs+DSV) around every run — pure
            // per-pass-fire overhead for a section that only rebinds vb/ib/ps-t and draws. Everything it
            // does touch is saved/restored by hand.
            P.Append($"[CommandListDraw_{sfx}]\n"
                   + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
            // texture binds only when some submesh of this pipeline asks for one: a pipeline whose every
            // submesh inherits keeps every original map, so it needs no probe and no ps-t save/restore
            bool donorTexed = DonorTexed(pipe.SubMaps);
            if (donorTexed)
                foreach (int s in ProbeSlots) P.Append($"Resource_SaveT{s} = ref ps-t{s}\n");
            P.Append($"vb0 = Resource_NewPosed_{sfx}\nvb1 = Resource_NewVB1_{sfx}\nvb3 = Resource_NewPosed_{sfx}\nib = Resource_NewIB_{sfx}\n");
            EmitDrawTextures(P, donorTexed, pipe.SubMaps, pipe.Draws, slotTags, texRes);
            // vb3 gets its own save: it is rebound to the skin output above, and restoring it from the vb0
            // save would hand the game whatever vb0 held — wrong whenever they differed
            P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
            if (donorTexed)
                foreach (int s in ProbeSlots) P.Append($"ps-t{s} = Resource_SaveT{s}\n");
            // The routed per-range lists: one per donor submesh, the full list's save/bind/restore shape
            // drawing only that range. Referenced by the per-submesh sections a routed capture site emits;
            // a site a twin guard kept on the full list leaves its per-range lists unreferenced and inert.
            if (units.Ordered.Any(u => u.RoutedDraws.Any(rd => !rd.IsRigid && rd.Sfx == sfx)))
                for (int di = 0; di < pipe.Draws.Count; di++)
                {
                    P.Append($"\n[CommandListDrawS{di}_{sfx}]\n"
                           + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
                    if (donorTexed)
                        foreach (int s in ProbeSlots) P.Append($"Resource_SaveT{s} = ref ps-t{s}\n");
                    P.Append($"vb0 = Resource_NewPosed_{sfx}\nvb1 = Resource_NewVB1_{sfx}\nvb3 = Resource_NewPosed_{sfx}\nib = Resource_NewIB_{sfx}\n");
                    EmitDrawTextures(P, donorTexed, pipe.SubMaps, pipe.Draws, slotTags, texRes, only: di);
                    P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
                    if (donorTexed)
                        foreach (int s in ProbeSlots) P.Append($"ps-t{s} = Resource_SaveT{s}\n");
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
            if (rigidTexed)
                foreach (int s in ProbeSlots) P.Append($"Resource_SaveT{s} = ref ps-t{s}\n");
            // vb3 takes the position stream like vb0, matching what a pooled draw binds: the passes that
            // read it read positions.
            P.Append($"vb0 = Resource_RigidVB0_{r.Sfx}\n");
            if (r.Vb1Stride is not null) P.Append($"vb1 = Resource_RigidVB1_{r.Sfx}\n");
            P.Append($"vb3 = Resource_RigidVB0_{r.Sfx}\nib = Resource_RigidIB_{r.Sfx}\n");
            EmitDrawTextures(P, rigidTexed, r.SubMaps, r.Draws, slotTags, texRes);
            P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
            if (rigidTexed)
                foreach (int s in ProbeSlots) P.Append($"ps-t{s} = Resource_SaveT{s}\n");
            // the rigid twin of the pooled per-range lists above
            if (r.Hashes.Any(h => r.ShapesByHash?.GetValueOrDefault(h) is { } hs
                && hs.Shapes.Count(sh => sh.Count > 0) > 1 && !guards.ContainsKey(h)))
                for (int di = 0; di < r.Draws.Count; di++)
                {
                    P.Append($"\n[CommandListRigidS{di}_{r.Sfx}]\n"
                           + "Resource_SaveVB0 = ref vb0\nResource_SaveVB1 = ref vb1\nResource_SaveVB3 = ref vb3\nResource_SaveIB = ref ib\n");
                    if (rigidTexed)
                        foreach (int s in ProbeSlots) P.Append($"Resource_SaveT{s} = ref ps-t{s}\n");
                    P.Append($"vb0 = Resource_RigidVB0_{r.Sfx}\n");
                    if (r.Vb1Stride is not null) P.Append($"vb1 = Resource_RigidVB1_{r.Sfx}\n");
                    P.Append($"vb3 = Resource_RigidVB0_{r.Sfx}\nib = Resource_RigidIB_{r.Sfx}\n");
                    EmitDrawTextures(P, rigidTexed, r.SubMaps, r.Draws, slotTags, texRes, only: di);
                    P.Append("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n");
                    if (rigidTexed)
                        foreach (int s in ProbeSlots) P.Append($"ps-t{s} = Resource_SaveT{s}\n");
                }
        }
        return P.ToString();
    }

    /// <summary>The texture half of a replacement's draw list, shared by both routes so a rigid draw and a
    /// pooled one bind identically: the slot probe that reads where the replaced part's own stock maps are
    /// bound right now, then per submesh the binds that row asked for and its drawindexed.</summary>
    static void EmitDrawTextures(StringBuilder P, bool donorTexed, SubmeshMaps?[] subMaps,
        IReadOnlyList<(int Count, int Start, int Base)> draws, IReadOnlyList<StockMapTag> slotTags,
        IReadOnlyDictionary<string, string> texRes, int only = -1)
    {
        // an ordered list, not a map: the emitted text is a pinned contract, so bind order is fixed
        var slotVars = new[]
        {
            (Kind: StockMapKind.Albedo, Var: VarAlbedoSlot),
            (Kind: StockMapKind.Normal, Var: VarNormalSlot),
            (Kind: StockMapKind.Rmo, Var: VarRmoSlot),
        };
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
                _ => VarRmoSlot,
            };
            foreach (int s in ProbeSlots)
            {
                P.Append($"${VarProbe} = ps-t{s}\n");
                P.Append($"if ${VarProbe} == {FilterAlbedo}\n${VarAlbedoSlot} = {s}\nendif\n");
                P.Append($"if ${VarProbe} == {FilterNormal}\n${VarNormalSlot} = {s}\nendif\n");
                P.Append($"if ${VarProbe} == {FilterRmo}\n${VarRmoSlot} = {s}\nendif\n");
                foreach (var t in slotTags)
                    P.Append($"if ${VarProbe} == {RetexTag(t.Hash)}\n${SlotVarFor(t.Kind)} = {s}\nendif\n");
            }
        }
        // Per submesh, per kind: bind what THAT submesh's row asked for, at whichever slot the probe
        // found the anchor's map of that kind in. The list is sequential and one binding outlives the
        // draw that set it, so a slot an earlier submesh bound is put back from its save when a later
        // one inherits — otherwise an untouched submesh would draw wearing its neighbour's map.
        // A single-range list (only >= 0) emits exactly that submesh's binds and draw: alone in its
        // list, it inherits from the game's own binds rather than a neighbour's leftovers.
        var bound = new Dictionary<StockMapKind, string?>();   // null/absent = the game's own bind
        for (int di = only >= 0 ? only : 0; di < (only >= 0 ? only + 1 : draws.Count); di++)
        {
            if (donorTexed)
                foreach (var (kind, slotVar) in slotVars)
                {
                    var want = Slot(subMaps, di, kind);
                    string? res = want.IsNeutral ? NeutralResource(kind)
                        : want.File is { } fn ? texRes[fn] : null;
                    bound.TryGetValue(kind, out var had);   // absent reads null: still the game's own
                    if (string.Equals(had, res, StringComparison.Ordinal)) continue;
                    bound[kind] = res;
                    foreach (int s in ProbeSlots)
                        P.Append($"if ${slotVar} == {s}\nps-t{s} = {res ?? $"Resource_SaveT{s}"}\nendif\n");
                }
            P.Append($"drawindexed = {draws[di].Count}, {draws[di].Start}, {draws[di].Base}\n");
        }
    }

    /// <summary>The retexture sections: one <c>[Resource_Rtx*]</c> per distinct replacement (copied into
    /// <paramref name="outDir"/>) and one <c>[TextureOverride_Retex_*]</c> per entry, keyed on the stock
    /// texture's hash and rebinding it with <c>this =</c>. No slot binds, no save/restore — the swap
    /// happens where the resource is bound, not around a draw. Two entries on one hash throw, as do
    /// distinct sources sharing a basename (a silent last-copy-wins would ship the wrong texture).
    ///
    /// <para>An entry whose hash a twin guard also probes for carries that guard's <c>filter_index</c> tag
    /// on its own section, ahead of the gate — one hash owns one section, and the tag has to answer
    /// whether or not the retexture's key is on.</para></summary>
    string RetexIni(IReadOnlyList<RetexEntry> entries, string outDir, string? modKey,
        IReadOnlyList<ScopedRetexEntry>? scoped, CaptureUnits units, Sightings sightings,
        IReadOnlyDictionary<string, RigidEmission>? rigidOwner = null,
        IEnumerable<TwinGuard>? twinGuards = null,
        IReadOnlyDictionary<string, StockMapKind>? slotTagKinds = null)
    {
        var P = new StringBuilder();

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
        foreach (var e in entries) { ClaimHash(e.Name, e.Hash); DeclareFile(e.DdsFile); }
        foreach (var e in scoped ?? Array.Empty<ScopedRetexEntry>())
        {
            if (e.Images.Count == 0)
                throw new InvalidOperationException(
                    $"draw-scoped retexture '{e.Name}' on texture hash {e.StockHash} carries no image");
            ClaimHash(e.Name, e.StockHash);
            foreach (var img in e.Images) DeclareFile(img.DdsFile);
        }
        // the scoped sections' ps-t saves, shared across every scoped anchor section
        if (scoped is { Count: > 0 })
            foreach (int s in ProbeSlots) P.Append($"[Resource_RtxSave{s}]\n");
        P.Append("\n");

        // The stock textures a twin guard probes for by a value derived from the hash. A retexture's own
        // section already owns those hashes, so it carries the tag rather than letting a second section
        // mint itself on one — the ini parse drops the second, and which of the two survived could not
        // be predicted.
        var guardList = (twinGuards ?? Array.Empty<TwinGuard>()).ToList();
        var mintedTwinTags = MintedTwinTagHashes(guardList);
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
            var gate = new Gate(modKey, e.ToggleKey);
            gate.Open(P);
            P.Append($"this = {texRes[e.DdsFile]}\n");
            gate.Close(P);
            P.Append("\n");
        }

        // One tag per stock texture a twin guard probes for and nothing else in the build tags. A hash
        // the scoped retextures already tag carries that section instead: both derive the same value
        // from the hash, and a second section on one hash is dropped at parse time. A slot-tagged hash
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
        {
            // one tag per stock texture: the derived filter_index the anchor probes read back.
            // match_priority marks the deliberate cross-mod duplicate (same hash, same value) so the
            // duplicate-hash warning stays quiet and ties resolve deterministically.
            foreach (var hash in scoped.Select(e => e.StockHash).Distinct(StringComparer.OrdinalIgnoreCase))
                P.Append($"[TextureOverride_RetexTag_{hash}]\nhash = {hash}\n"
                       + $"filter_index = {RetexTag(hash)}\nmatch_priority = 100\n\n");

            // one section per distinct anchor mesh; each scoped texture of that anchor probes and binds
            // inside it. Saves and post-restores are UNCONDITIONAL — restoring an untouched slot to its
            // own just-saved ref is a no-op, and a gated-off draw must not restore stale refs — so only
            // the binds sit under the keys and the latch.
            var anchors = new List<(string Hash, string Suffix)>();
            var perAnchor = new Dictionary<string,
                List<(ScopedRetexEntry E, ScopedRetexImage I, ScopedAnchor A)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in scoped)
                foreach (var img in e.Images)
                foreach (var a in img.Anchors)
                {
                    if (!perAnchor.TryGetValue(a.Hash, out var list))
                    {
                        perAnchor[a.Hash] = list = new List<(ScopedRetexEntry, ScopedRetexImage, ScopedAnchor)>();
                        anchors.Add((a.Hash, a.Suffix));
                    }
                    list.Add((e, img, a));
                }
            var usedSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ibHash, first) in anchors)
            {
                var body = new List<string>();
                foreach (int s in ProbeSlots) body.Add($"Resource_RtxSave{s} = ref ps-t{s}");
                // ONE probe per stock texture, ahead of every image that binds through it: a bind replaces
                // the tagged resource in the slot, so a probe run after one finds no tag and its own image
                // would never bind. The images then differ only in their gate.
                foreach (var group in perAnchor[ibHash].GroupBy(x => x.E.StockHash, StringComparer.OrdinalIgnoreCase))
                {
                    int tag = RetexTag(group.Key);
                    body.Add($"${VarRetexSlot} = -1");
                    foreach (int s in ProbeSlots)
                    {
                        body.Add($"${VarRetexProbe} = ps-t{s}");
                        body.Add($"if ${VarRetexProbe} == {tag}");
                        body.Add($"${VarRetexSlot} = {s}");
                        body.Add("endif");
                    }
                    foreach (var (_, img, a) in group)
                    {
                        var gate = new Gate(new[] { modKey, img.ToggleKey },
                            a.Latch is null ? null : new[] { GateVar(a.Latch) });
                        body.AddRange(gate.Wrap(ProbeSlots.SelectMany(s => new[]
                        {
                            $"if ${VarRetexSlot} == {s}", $"ps-t{s} = {texRes[img.DdsFile]}", "endif",
                        })));
                    }
                }
                foreach (int s in ProbeSlots) body.Add($"post ps-t{s} = Resource_RtxSave{s}");

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
                // duplicate-named sections drop silently at parse time, so a suffix two anchors share
                // (one character's two outfits, same part token) gets disambiguated here
                string suffix = first;
                while (!usedSuffixes.Add(suffix)) suffix += "_";
                OpenTextureOverride(P, $"RetexScope_{suffix}", ibHash);
                if (sightings.ByHash.TryGetValue(ibHash, out var seen))
                    foreach (var line in seen) P.Append(line).Append('\n');
                foreach (var line in body) P.Append(line).Append('\n');
                P.Append("\n");
            }
        }
        return P.ToString();
    }

    /// <summary>A hash both hidden and claimed as a scoped retexture's anchor would mint two
    /// TextureOverride sections, and the ini parse keeps only one, decided by section order. Refused
    /// instead, on both build routes.</summary>
    static void RefuseHiddenScopedAnchors(IReadOnlyList<string> hides,
        IReadOnlyList<ScopedRetexEntry>? scoped)
    {
        if (hides.Count == 0 || scoped is not { Count: > 0 }) return;
        var hidden = new HashSet<string>(hides, StringComparer.OrdinalIgnoreCase);
        foreach (var e in scoped)
            foreach (var img in e.Images)
                foreach (var a in img.Anchors)
                    if (hidden.Contains(a.Hash))
                        throw new InvalidOperationException(
                            $"'{a.Suffix}' is hidden, and '{e.Name}' is retextured on its draws. One mesh "
                            + "takes one override section, so the build can't emit both. Drop the Hide or "
                            + "that texture edit");
    }

    /// <summary>The <c>[Constants]</c> declaration of every distinct key — the ONE place a key variable is
    /// declared, so both build routes start a key the same way. A key starts ON unless
    /// <paramref name="startingOff"/> names it. A toggle is PER-SESSION: this runs at every load, so the
    /// value written here is where the next session starts.</summary>
    static string KeyDeclarations(IReadOnlyList<string> keys, IReadOnlyCollection<string>? startingOff)
    {
        var off = new HashSet<string>(ModKeys.Distinct(startingOff ?? Array.Empty<string>()),
            StringComparer.Ordinal);
        var P = new StringBuilder();
        foreach (var k in keys) P.Append($"global ${ModKeys.VariableFor(k)} = {(off.Contains(k) ? 0 : 1)}\n");
        return P.ToString();
    }

    /// <summary>The key sections — one pair per distinct key, whatever the tier (two changes on one key
    /// share one variable). Each flips its variable with <c>$var = 1 - $var</c>, start-agnostic. Emits
    /// NOTHING when no key is bound.
    ///
    /// <para>The flip lives in a <c>[CommandList…]</c> the <c>[Key…]</c> section <c>run</c>s: 3DMigoto
    /// parses a <c>[Key…]</c> section as a KeyOverride, not a command list, so a variable assignment
    /// written there is dropped at parse time and the press does nothing.</para>
    ///
    /// <para>A key with no modifiers is bound <c>no_modifiers</c>: a bare <c>key = F6</c> also fires on
    /// CTRL+F6, which would fire two toggles at once beside a distinct CTRL F6 binding.</para></summary>
    static string KeysIni(string? modKey, IEnumerable<string?> changeKeys)
    {
        var keys = ModKeys.Distinct(new[] { modKey }.Concat(changeKeys));
        if (keys.Count == 0) return "";
        var P = new StringBuilder();
        foreach (var k in keys)
        {
            string v = ModKeys.VariableFor(k);
            // normalized keys are modifier tokens then ONE key token, so a single token means none named
            string binding = k.Contains(' ') ? k : $"no_modifiers {k}";
            P.Append($"[Key_{v}]\nkey = {binding}\nrun = CommandListKey_{v}\n\n");
            P.Append($"[CommandListKey_{v}]\n${v} = 1 - ${v}\n\n");
        }
        return P.ToString();
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
        IReadOnlySet<string>? retexturedHashes = null, IReadOnlyList<string>? stickyFlags = null)
    {
        var keys = ModKeys.Distinct(new[] { modKey }.Concat(changeKeys ?? Array.Empty<string?>()));
        var P = new StringBuilder($"[Constants]\nglobal ${VarProbe} = 0\nglobal ${VarAlbedoSlot} = 0\n"
            + $"global ${VarNormalSlot} = 0\nglobal ${VarRmoSlot} = 0\n");
        // declared here and written only by the guard probes: the [Present] resets below leave them
        // alone, which is what carries a verdict across the passes that bind no identifying texture
        foreach (var v in twinVars ?? Array.Empty<string>()) P.Append($"global ${v} = 0\n");
        // the multi-verdict guards' scratch, rewritten at every guard it opens rather than carried
        if (twinScratch) P.Append($"global ${VarTwinOk} = 0\n");
        foreach (var f in perFrameFlags ?? Array.Empty<string>()) P.Append($"global ${f} = 0\n");
        // declared beside the per-frame flags and left out of the [Present] reset below: what they record
        // is that a capture has happened at all, which is a per-SESSION fact
        foreach (var f in stickyFlags ?? Array.Empty<string>()) P.Append($"global ${f} = 0\n");
        if (scopedRetex) P.Append($"global ${VarRetexProbe} = 0\nglobal ${VarRetexSlot} = 0\n");
        foreach (var l in latches ?? Array.Empty<WitnessLatch>())
            P.Append($"global ${GateVar(l.Name)} = 0\nglobal ${SeenVar(l.Name)} = 0\n");
        P.Append(KeyDeclarations(keys, keysStartingOff));
        P.Append("\n");
        if (perFrameFlags is { Count: > 0 } || latches is { Count: > 0 })
        {
            P.Append("[Present]\n");
            foreach (var f in perFrameFlags ?? Array.Empty<string>()) P.Append($"${f} = 0\n");
            foreach (var l in latches ?? Array.Empty<WitnessLatch>())
                P.Append($"${GateVar(l.Name)} = ${SeenVar(l.Name)}\n${SeenVar(l.Name)} = 0\n");
            P.Append("\n");
        }
        P.Append(KeysIni(modKey, changeKeys ?? Array.Empty<string?>()));
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
        Func<string, Matrix4x4?> conversion)
    {
        var jobs = new List<(string Name, string Dir, string? OpKey)>();
        var seen = new HashSet<(string, string)>();
        foreach (var pipe in req.Pipelines)
        {
            foreach (var p in pipe.Parts)
                if (seen.Add((p.Name, p.DumpDir))) jobs.Add((p.Name, p.DumpDir, p.OpKey));
            foreach (var t in pipe.Tiers ?? Array.Empty<PoolTier>())
                if (seen.Add((t.Name, t.DumpDir))) jobs.Add((t.Name, t.DumpDir, t.OpKey));
            foreach (var m in GroupMeshes(pipe))
                if (seen.Add((m.Name, m.DumpDir))) jobs.Add((m.Name, m.DumpDir, m.OpKey));
        }

        var solved = new ConcurrentDictionary<(string, string), OperatorSolve>();
        Parallel.ForEach(jobs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, CpuLimit ?? Environment.ProcessorCount) },
            job =>
            {
                OperatorSolve result;
                try
                {
                    string? key = OperatorCacheKey(job.OpKey, job.Name, conversion(job.Dir));
                    var art = key is null ? null : ReadCachedOperator(OperatorCachePath(key), key);
                    if (art is null)
                    {
                        art = BuildOperator(load(job.Dir), unionInput(job.Dir).Hashes, job.Name);
                        if (key is not null) WriteCachedOperator(OperatorCachePath(key), key, art);
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
    /// <see cref="Weak"/> membership comes from the dense residual and nothing else: a deterministic
    /// synthetic-palette measurement that depends only on bind positions and weights, never on pose. The
    /// slim rows carry a separate gate; a bone that cannot hold it widens to every vertex on its own,
    /// which is why no bone's conditioning can decline slimming for the part.
    /// A weak bone's rows AND its Sel segment are replaced by its <see cref="Tie"/> bone's, so its geometry
    /// rides that bone rigidly instead of taking a min-norm estimate — valid without space conversion,
    /// since every palette row maps the mesh's bind space to the posed space. Tie = -1 when the mesh has no
    /// sound bone at all: the bone keeps its own rows, and tier scatter sentinels it to its lod0
    /// row.</summary>
    sealed record OperatorArt(float[] Cpinv, bool[] Weak, int[] Tie, uint[] Hashes,
        IReadOnlyList<string> Diagnostics, uint[]? Sel, uint[]? Off, int N);

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

    static OperatorArt BuildOperator(StreamsLoad load, uint[] hashes, string name)
    {
        int nb = load.Nb, n = load.P.GetLength(0);
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
        int kStart = Math.Max(1, Math.Min(KStart, n));
        int kCap = Math.Max(1, Math.Min(KCap, n));
        var slim = SlimOperator(load, nb, kStart, kCap, weak, pinv);
        var picked = slim.Picked;
        var slimRows = slim.Rows;
        var slimErr = slim.Err;

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

        var tie = new int[nb];
        for (int b = 0; b < nb; b++)
        {
            tie[b] = b;
            if (!weak[b]) continue;
            // strongest co-riding sound bone; support-centroid proximity when co-weight is negligible
            var co = new double[nb];
            for (int v = 0; v < n; v++)
            {
                double wb = 0;
                for (int k = 0; k < 4; k++) if (load.BI[v, k] == b && load.W[v, k] > wb) wb = load.W[v, k];
                if (wb <= 0) continue;
                for (int k = 0; k < 4; k++)
                {
                    int c2 = load.BI[v, k];
                    if (load.W[v, k] > 0 && c2 != b && !weak[c2]) co[c2] += wb * load.W[v, k];
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
                    if (weak[c2] || wsum[c2] <= 0) continue;
                    double d = 0;
                    for (int j = 0; j < 3; j++) { double dd = centroid[b, j] - centroid[c2, j]; d += dd * dd; }
                    if (d < bestD) { bestD = d; best = c2; }
                }
            }
            tie[b] = best;                             // -1 = no sound bone anywhere on this mesh
        }

        // The tie copies operator rows and (when slim) the anchor-vertex segment together — slim
        // coefficients are meaningless without the vertices they index — so it is applied to the SELECTION
        // before the widths are read off it, and the tied bone's block ends up the same width as its
        // target's.
        for (int b = 0; b < nb; b++)
        {
            if (!weak[b] || tie[b] < 0) continue;
            picked[b] = picked[tie[b]];
            slimRows[b] = slimRows[tie[b]];
        }

        // Slim ships when it is SMALLER, and that is the only verdict left: a bone the anchor-local solve
        // cannot hold widens to the whole mesh by itself, so no bone's conditioning can decline the part.
        // Slim ships three buffers — 4 float rows of `width` per bone, the anchor indices those coefficients
        // are meaningless without, and the two-uint offset entry that locates both.
        long slimBytes = 8L * nb;
        for (int b = 0; b < nb; b++) slimBytes += (16L + 4L) * picked[b].Length;
        long denseBytes = 16L * nb * n;
        bool shipsSlim = slimBytes < denseBytes;

        for (int b = 0; b < nb; b++)
        {
            if (!weak[b] || tie[b] < 0) continue;
            int best = tie[b];
            // a tie inherits its target's width, and a target at the full vertex count makes the tied bone
            // cost 20·n bytes too — the size the rest of the message says nothing about
            string width = shipsSlim && picked[b].Length == n ? $" · at dense width ({n} rows)" : "";
            // the reported number is the dense residual — the verdict that produced the tie; the bone's
            // slim defect describes rows the tie overwrites, so it never ships
            diagnostics.Add($"{name}: bone 0x{hashes[b]:x8} recovers ill-conditioned from this mesh "
                    + $"(err {err[b]:g2}) — tied rigidly to co-riding bone 0x{hashes[best]:x8}{width}");
        }
        for (int b = 0; b < nb; b++)
            if (weak[b] && tie[b] < 0)
                diagnostics.Add($"{name}: bone 0x{hashes[b]:x8} is weakly supported (err {err[b]:g2}) and has "
                        + "no sound bone to ride. Donor weight on it may distort");

        float[] op;
        uint[]? sel;
        uint[]? off;
        if (shipsSlim)
        {
            (op, sel, off) = AssembleSlim(picked, slimRows, nb);
            if (slim.LastK > kStart) diagnostics.Add($"{name}: anchor rows escalated to K={slim.LastK} to hold conditioning");
            for (int b = 0; b < nb; b++)
                if (slim.DenseWidth[b])
                    diagnostics.Add($"{name}: bone 0x{hashes[b]:x8} ships at dense width — {picked[b].Length} rows "
                            + $"(defect {slimErr[b]:g2} at K={slim.LastK})");
            // what shipped next to what it replaced: a triager comparing a slim build against a dense one
            // needs both numbers. Bones whose rows the tie or the dense width replaced are not described by
            // their slim defect, so they are not candidates for the worst.
            int worst = -1;
            for (int b = 0; b < nb; b++)
                if (!weak[b] && !slim.DenseWidth[b] && (worst < 0 || slimErr[b] > slimErr[worst])) worst = b;
            if (worst >= 0)
                diagnostics.Add($"{name}: slim operator ships · worst defect {slimErr[worst]:g2} (dense {err[worst]:g2})");
        }
        else
        {
            // a size verdict says nothing about conditioning, so it says nothing
            (op, sel, off) = (pinv.Materialize(), null, null);
            for (int b = 0; b < nb; b++)
                if (weak[b] && tie[b] >= 0)
                    for (int r = 0; r < 4; r++)
                        Array.Copy(op, (4 * tie[b] + r) * n, op, (4 * b + r) * n, n);
        }
        return new OperatorArt(op, weak, tie, hashes, diagnostics, sel, off, n);
    }

    /// <summary>Pack the per-bone selections and rows into the ragged triple the mod ships: bone b's block
    /// starts at element <c>base</c> of Sel and float <c>4*base</c> of Cpinv, is <c>width</c> wide, and
    /// <c>Off[2b], Off[2b+1]</c> carry the pair. Blocks tile both buffers in bone order with no padding and
    /// no gaps.</summary>
    static (float[] Cpinv, uint[] Sel, uint[] Off) AssembleSlim(int[][] picked, double[][][] rows, int nb)
    {
        int total = 0;
        for (int b = 0; b < nb; b++) total += picked[b].Length;
        var cp = new float[4 * total];
        var sel = new uint[total];
        var off = new uint[2 * nb];
        int bas = 0;
        for (int b = 0; b < nb; b++)
        {
            int width = picked[b].Length;
            off[2 * b] = (uint)bas;
            off[2 * b + 1] = (uint)width;
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
    /// row, which recovers the zero palette row the dense operator gives it.</summary>
    static SlimSolve SlimOperator(StreamsLoad load, int nb, int kStart, int kCap, bool[] denseWeak,
        PoolMath.PInvFactors densePinv)
    {
        int n = load.P.GetLength(0);
        // per-bone candidate counts: escalation cannot help a bone whose selection already saturated — a
        // bigger K re-selects the same vertices and re-solves an identical system, for double the width
        var candCount = new int[nb];
        // one buffer for the whole loop: sc resets per vertex, so only the entries this vertex wrote are read
        Span<int> seen = stackalloc int[4];
        for (int v = 0; v < n; v++)
        {
            int sc = 0;
            for (int j = 0; j < 4; j++)
            {
                if (load.W[v, j] <= 0) continue;
                int b2 = load.BI[v, j];
                if (b2 < 0 || b2 >= nb) continue;
                bool dup = false;
                for (int s = 0; s < sc; s++) if (seen[s] == b2) dup = true;
                if (!dup) { seen[sc++] = b2; candCount[b2]++; }
            }
        }

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
            for (int b = 0; b < nb; b++) SolveBone(b);
            bool ok = true;
            for (int b = 0; b < nb && ok; b++) ok = denseWeak[b] || !FailsSlimGate(err[b]);
            if (ok || k >= kCap) break;
            int maxCand = 0;
            for (int b = 0; b < nb; b++)
                if (!denseWeak[b] && FailsSlimGate(err[b])) maxCand = Math.Max(maxCand, candCount[b]);
            if (k >= maxCand) break;               // every failing bone is saturated
            k = Math.Min(k * 2, kCap);             // never overshoot the cap (nor the vertex count)
        }

        // a dense-sound bone the local solve never held widens to the whole mesh, taking the dense
        // operator's own rows — the ones the gate is calibrated against. Only this bone pays for it.
        var identity = (int[]?)null;
        for (int b = 0; b < nb; b++)
        {
            if (denseWeak[b] || !FailsSlimGate(err[b])) continue;
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
            binds[h] = bp;
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
