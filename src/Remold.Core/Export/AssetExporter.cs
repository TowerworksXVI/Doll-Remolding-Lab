using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Skeleton;
using Remold.Core.Textures;

namespace Remold.Core.Export;

/// <summary>One file written by an export (or an attempt that failed). <see cref="Bundle"/> is the live
/// bundle the asset was READ from — the package must write the edit back to that same bundle, not
/// re-resolve the name (which can hit a duplicate stub). <see cref="OriginalPath"/> is the pristine copy
/// under <c>originals/</c>, so edit-tolerance (delta, outline restore) survives a restart and a game
/// update. <see cref="BakedRest"/> (mesh only) is the scene-rest uprighting baked into the glb
/// (<see cref="Mesh.RestBake"/>), undone at package build; null = nothing baked.
/// <see cref="TextureMeta"/> (texture only) is the live target's format/dimensions/mip count captured at
/// export so the package build can pre-encode offline.</summary>
public readonly record struct ExportedFile(string Kind, string AssetName, string Path, bool Ok, string? Note,
    string? Bundle = null, string? OriginalPath = null,
    IReadOnlyList<LodSlot>? LodSiblings = null, IReadOnlyList<string>? Users = null,
    IReadOnlyList<float>? BakedRest = null, Bundles.BundleReader.TextureMeta? TextureMeta = null,
    string? Source = null,
    // mesh only — the exact path-id selector, set on smr-body parts (enemy bundles ship same-named mesh
    // copies, so the name alone can select the wrong one). Null on recipe-backed parts.
    long? PathId = null);

public sealed class ExportReport
{
    public required string OutputDir { get; init; }
    public List<ExportedFile> Files { get; } = new();
    public int MeshCount => Files.Count(f => f.Kind == "mesh" && f.Ok);
    // a shared texture stages one entry per using part — count DISTINCT names so the tally reads
    // "textures written", not "texture references"
    public int TextureCount => Files.Where(f => f.Kind == "texture" && f.Ok)
        .Select(f => f.AssetName).Distinct(StringComparer.Ordinal).Count();
    /// <summary>The pristine original (Unity-space) mesh per exported glb path — the Edit step's import
    /// tolerance (vertex/slot delta, outline restore) without re-decoding from the game.</summary>
    public Dictionary<string, Mesh.UnityMesh> OriginalMeshByPath { get; } = new();
    /// <summary>The part tokens that reached commit — a prefix of the requested set when cancelled. The
    /// selection ledger updates from THIS, never the requested set, so a part abandoned by a cancel isn't
    /// recorded as exported.</summary>
    public List<string> CompletedParts { get; } = new();
}

/// <summary>
/// Exports a part's high-detail (lod0) mesh + its textures to a mod working folder, recipe-exact
/// (<see cref="ExportRecipePart"/>): meshes to <c>meshes/</c> as <c>.glb</c>, textures to
/// <c>textures/</c> as <c>.png</c>. Textures resolve renderer-first
/// (<see cref="Textures.PartTextureResolver"/>); a part whose renderer binds no texture reports the miss
/// loudly and exports the mesh untextured.
/// </summary>
public static class AssetExporter
{
    /// <summary>Filename of the optional whole-outfit combined glb (all skinned parts, one union-skeleton
    /// armature) written alongside the per-part glbs in <c>meshes/</c>. Deterministic so the Edit pane can
    /// find it without project plumbing.</summary>
    public const string CombinedGlbName = "_combined.glb";

    /// <summary>One part of the SUBJECT as the rigged export's tail filter weighs it — every part the
    /// subject has, not only the ones this run writes a glb for. <paramref name="Mesh"/> is the
    /// representative <c>_lod0</c> slot's name, which is both the key a filtered part is named by (the same
    /// key <see cref="Migoto.PoolDerive.PoolCandidates"/> matches a Replace's target on) and the name its
    /// mesh is looked up in <paramref name="SourceBundle"/> under. <paramref name="Token"/> is the part
    /// token presence is classified from, which is NOT the slot name.
    ///
    /// <para><paramref name="Visibility"/> carries the part's PREFAB-RESIDENT marker only. The build merges
    /// that with the timeline-derived half (<c>ModBuilder</c>'s <c>VisibilityOf</c>), but timelines are a
    /// build-time input the workbench model never reads, so a part withheld only by timeline data is
    /// admitted here. Deliberate: the export over-offers by exactly that part's bones and the build-time
    /// posed gate refuses paint on them, which is the safe direction — under-offering would hide bones a
    /// build would have accepted.</para></summary>
    public sealed record RosterPart(string Mesh, string Token, string SourceBundle, long PathId,
        bool CastsShadows, VisibilityOverride Visibility);

    /// <summary>The candidacy roster a rigged export filters its appended bone tail against: every part of
    /// the subject, in the subject model's own order, plus the wardrobe <paramref name="Scheme"/> presence
    /// is classified against (null = not modular, or the tables wouldn't read, which only widens the offer).
    /// Null roster at the export means candidacy is unknown and the whole subject skeleton is offered, the
    /// behaviour before this filter existed.</summary>
    public sealed record SubjectRoster(IReadOnlyList<RosterPart> Parts,
        IReadOnlyList<Tables.PartScheme.Slot>? Scheme = null,
        bool PartsPoolAlone = false);

    /// <summary>The <c>rosterDegraded</c> entry a rigged build adds when an export fell back to offering the
    /// WHOLE skeleton because candidacy was unknown for it — no row of the roster measured, or the exported
    /// part absent from the rows that did. Parenthesised so it can never collide with a slot name, which is
    /// what every other entry in that collection is.</summary>
    internal const string RosterUnfiltered = "(candidacy unknown)";

    /// <summary>Why a roster row that produced no candidacy is held back, for the wardrobe-coverage rule
    /// that reads the held-back list. It never reaches a modder: the build states its own reasons, one per
    /// part, and this route only decides whether a slot may certify coverage.</summary>
    private const string RosterUnmeasured = "its mesh or its weights couldn't be read";

    /// <summary>Filename a combined session's Blender send lands under, declared to the bridge through the
    /// session file. Distinct from <see cref="CombinedGlbName"/> so a send never writes over the published
    /// combined glb, whose fingerprint and map sidecar describe the app's own build.</summary>
    public const string CombinedSendGlbName = "_combined.send.glb";

    /// <summary>A stable fingerprint of the inputs that built a combined Blender glb: catalog version plus
    /// the ordered per-part (token, bundle, object-name) identity, plus — for a part taken from its EDITED
    /// workspace glb — that file's own identity (length + last-write time), plus the same identity for every
    /// texture PNG the build would embed. Persisted beside the cached <see cref="CombinedGlbName"/> so an
    /// open reuses it ONLY while the inputs still match; a part added/removed/re-addressed, a game update,
    /// a change to included edited geometry, or a repainted map forces a rebuild. Deterministic (no hashing)
    /// so it round-trips and is inspectable; parts compare in the order given.
    /// <paramref name="parts"/> carries <c>EditedGlb</c> = the workspace glb path when the part is edited.
    /// A path whose stamp can't be read folds to a fixed marker, matching what an absent edit produces.
    ///
    /// <para><paramref name="texturePaths"/> is the maps' half (<see cref="EmbeddedTexturePaths"/> supplies
    /// it), stamped rather than event-driven: a card drop, an external editor and a revert all rewrite the
    /// file, and a stamp catches every route with no plumbing to forget. No default: an open that forgot
    /// the maps would hand the modder a glb with the pre-edit texture baked in, so a caller with none
    /// passes an empty sequence and says so.</para></summary>
    public static string CombinedFingerprint(string catalogVersion,
        IEnumerable<(string Token, string Bundle, string ObjectName, string? EditedGlb)> parts,
        IEnumerable<string> texturePaths)
    {
        var sb = new System.Text.StringBuilder();
        // v14: the version moves whenever the combined build's own output rules change — a cached glb built
        // to an older rule renders parts at poses this build no longer writes, or carries an armature this
        // build no longer draws, so it must rebuild rather than open. v7 moved the subject's unposed bones
        // from armature nodes to zero-weighted tail joints (MeshGltf.ExtraBone), so a v6 cache opens in
        // Blender with those bones as loose empties nothing can be painted against. v8 reduces an EDITED
        // part's re-read skin to the bones its geometry rides (MeshSkin.WeightedOnly): a v7 cache of a
        // session holding an edited part stands every later part's shared joints at that part's tail
        // worlds instead of their own. v9 filters the appended tail to the bones a build would actually
        // accept paint on (see SubjectRoster), so a v8 cache offers bones every send would be refused at.
        // v10 adds the wardrobe's own coverage to that tail (see PoolDerive.VariantGroups), so a v9 cache
        // leaves out bones a build now accepts paint on. v11 widens that tail again with the scene-context
        // pair's coverage (same seam), so a v10 cache leaves out bones a build now accepts paint on. v12
        // widens it once more: an unmeasurable sibling piece no longer kills its slot's coverage, so a v11
        // cache leaves out the bones those slots certify. v13 drops the last narrowing — coverage no longer
        // stops at what the POOL tables, since a group bone rides an appended palette slot of its own — so a
        // v12 cache leaves out every bone only the group's own members carry, which is most of them.
        // v14 admits every stored skin width (1–4) to candidacy measurement, so a v13 cache holds tails
        // and groups derived while the below-four siblings read as unmeasurable — an under-offer wherever
        // a roster carries one. v15 forms coverage from the unified variant×context predicate — a bone
        // certifies when every (variant, context) cell the target displays in holds an on-screen poser —
        // so a v14 cache leaves out bones the old per-slot and per-scene rules could not see a build now
        // accepts paint on.
        sb.Append("combined-v15\n").Append(catalogVersion).Append('\n');
        foreach (var p in parts)
        {
            sb.Append(p.Token).Append('\t').Append(p.Bundle).Append('\t').Append(p.ObjectName);
            if (p.EditedGlb is not null) sb.Append('\t').Append("edit:").Append(FileStamp(p.EditedGlb));
            sb.Append('\n');
        }
        sb.Append(TextureStamps(texturePaths));
        return sb.ToString();
    }

    /// <summary>The texture half of <see cref="CombinedFingerprint"/>, also the whole key the per-part open
    /// gate carries: one <c>tex\t&lt;path&gt;\t&lt;stamp&gt;</c> line per file. Sorted and de-duplicated
    /// HERE — a caller's enumeration order is whatever the project list happens to be, and two orderings of
    /// the same set must not read as two different specs.</summary>
    public static string TextureStamps(IEnumerable<string> texturePaths)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var path in texturePaths.Distinct(StringComparer.OrdinalIgnoreCase)
                                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            sb.Append("tex\t").Append(path).Append('\t').Append(FileStamp(path)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The workspace texture PNGs a Blender rebuild for <paramref name="meshNames"/> could embed:
    /// every materialized Texture2D target the project records a USER among those meshes for, resolved to
    /// the very file <see cref="ResolvePartPngs"/> would read. A target with no recorded users is INCLUDED:
    /// it can't be shown unrelated, and the failures are not symmetric — over-including costs one avoidable
    /// rebuild, under-including hands the modder a glb carrying the map they just replaced.</summary>
    public static IReadOnlyList<string> EmbeddedTexturePaths(ModProject project, IEnumerable<string> meshNames)
    {
        var meshes = new HashSet<string>(meshNames, StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var t in project.Targets)
        {
            if (t.AssetType != "Texture2D") continue;
            if (t.Users is { Count: > 0 } users && !users.Any(meshes.Contains)) continue;
            try { paths.Add(Path.GetFullPath(project.Resolve(t.ReplaceFile))); }
            catch { /* no RootDir / unresolvable relative — there is no file to stamp */ }
        }
        return paths;
    }

    /// <summary>A file's identity for the cache gate: length + last-write ticks, or <c>?</c> when unreadable.
    /// Cheap enough for every open (no content hash of a multi-megabyte glb).
    ///
    /// <para>The stamp is compared for EQUALITY, never ordered: a card drop and a revert both reach the file
    /// through <c>File.Copy</c>, which carries the SOURCE's last-write time over, so the ticks a workspace
    /// file ends up with are whatever the modder's file (or the stored original) carried, and can move
    /// backwards.</para></summary>
    private static string FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "?";
        }
        catch { return "?"; }
    }

    /// <summary>May the cached combined glb at <paramref name="combinedPath"/> be launched as-is? Only when
    /// the sidecar records BOTH the current input <paramref name="fingerprint"/> and the identity of the file
    /// this app published there. The output half guards the file itself: anything that lands on the path
    /// outside <see cref="PublishCombined"/> — a hand-dropped copy, a partial replace — rebuilds instead of
    /// opening as the app's own build.</summary>
    public static bool CombinedCacheHit(string combinedPath, string fingerprintPath, string fingerprint)
    {
        if (!File.Exists(combinedPath)) return false;
        string stored;
        try { stored = File.Exists(fingerprintPath) ? File.ReadAllText(fingerprintPath) : ""; }
        catch { return false; }   // unreadable sidecar = mismatch = rebuild
        return stored.Length > 0 && stored == SidecarText(fingerprint, combinedPath);
    }

    /// <summary>Whether a published combined glb has no <see cref="PreviewMaps"/> record beside it. A session
    /// classifies every image its send carries against that record; with none, untouched stock maps read as
    /// authored and each ships a redundant copy. A REUSED glb has to be asked this directly: the reuse skips
    /// the publish, which is the only other place the answer is known.</summary>
    public static bool CombinedMapRecordMissing(string combinedPath) =>
        !File.Exists(PreviewMaps.SidecarPath(combinedPath));

    /// <summary>May a combined glb built under these conditions be left cached for the next open? THE one
    /// home for that rule — <see cref="PublishCombined"/> publishes either way, and a false here means the
    /// caller drops the fingerprint sidecar so the next open rebuilds.
    ///
    /// <para>Two kinds of degrade, and only one blocks reuse. A row whose BYTES were unavailable this run
    /// (<paramref name="rosterRowsUnreadable"/>, e.g. a bundle the running game holds) may read differently
    /// the moment the lock clears, and the fingerprint cannot tell — so it blocks, as do unreadable roster
    /// inputs and a part that fell back to the game copy while carrying an edit (the fingerprint would claim
    /// an edit the file does not hold). A row whose CONTENT measured unmeasurable is NOT an input here and
    /// must never become one: the same catalog serves the same bytes to every rerun, so the cached tail is
    /// exactly what a rebuild would write. Every character's face row degrades that way, so gating on it
    /// keeps every combined session rebuilding on every open.</para></summary>
    public static bool CombinedCacheable(IReadOnlyCollection<string> partsFellBackToGame, bool hasRoster,
        IReadOnlyCollection<string> rosterRowsUnreadable, bool rosterInputsUnreadable) =>
        partsFellBackToGame.Count == 0 && hasRoster && rosterRowsUnreadable.Count == 0 && !rosterInputsUnreadable;

    /// <summary>What the fingerprint sidecar holds: the input fingerprint plus the published glb's own stamp.</summary>
    private static string SidecarText(string fingerprint, string combinedPath) =>
        fingerprint + "output\t" + FileStamp(combinedPath) + "\n";

    /// <summary>Publish a freshly-built combined glb and bless its fingerprint sidecar, atomically and ONLY on
    /// a real success. The temp's existence is the sole success signal — never a pre-existing destination, or
    /// a failed rebuild would bless the stale <paramref name="combinedPath"/>. Ordering: move the temp over
    /// the destination, and only THEN write the sidecar (the published file's own stamp plus the input
    /// fingerprint, see <see cref="CombinedCacheHit"/>), so a sidecar can never name a spec the file on disk
    /// doesn't match. Any failure leaves both old files untouched, so the next open rebuilds. Returns true
    /// when the fresh combined is published.
    ///
    /// <para>The map sidecar moves with the glb: the build writes it beside the temp, where nothing looks for
    /// it. Without it, every image in a returned glb resolves as authored — including untouched stock maps —
    /// and each ships a redundant explicit copy. A failed move clears the destination rather than leaving an
    /// older build's sidecar, which would resolve this glb's images against another one's origins. The glb
    /// publishes either way, so a build whose sidecar was lost calls <paramref name="onMapSidecarLost"/> —
    /// nothing else marks a session that can no longer tell a stock map from an authored one.</para></summary>
    public static bool PublishCombined(string tempPath, string combinedPath, string fingerprintPath, string fingerprint,
        Action? onMapSidecarLost = null)
    {
        if (!File.Exists(tempPath)) return false;   // build produced nothing — never bless the stale destination
        try { File.Move(tempPath, combinedPath, overwrite: true); }
        catch { return false; }   // couldn't replace (e.g. the destination is locked) — leave the old file + old fingerprint
        PublishMapSidecar(tempPath, combinedPath, onMapSidecarLost);
        try { File.WriteAllText(fingerprintPath, SidecarText(fingerprint, combinedPath)); }
        catch { /* best-effort — a missing/stale fingerprint forces a rebuild next time, never a stale reuse */ }
        return true;
    }

    /// <summary>Move the <see cref="PreviewMaps"/> sidecar from <paramref name="tempPath"/> onto
    /// <paramref name="finalPath"/>'s name; when there is none, or the move fails, remove whatever sidecar
    /// the destination still carries. <paramref name="onLost"/> fires only where a sidecar EXISTED and did
    /// not make it: a build that embedded no maps has no record to lose.</summary>
    private static void PublishMapSidecar(string tempPath, string finalPath, Action? onLost = null)
    {
        var from = PreviewMaps.SidecarPath(tempPath);
        var to = PreviewMaps.SidecarPath(finalPath);
        bool had = File.Exists(from);
        try
        {
            if (had) { File.Move(from, to, overwrite: true); return; }
        }
        catch { /* fall through to the clear below */ }
        try { if (File.Exists(to)) File.Delete(to); } catch { /* best effort */ }
        if (had) onLost?.Invoke();
    }


    /// <summary>
    /// Recipe-exact single-part materialization for the Outfit Workbench route. Reads the part's mesh by the
    /// assembly prefab RECIPE's exact identity — the renderer slot name (<see cref="RecipePart.SlotName"/>)
    /// in the logical bundle the CATALOG pins for the recipe's mesh address
    /// — with NO name-convention fallback of any kind. A CROSS-PREFIX part needs this: an alt outfit whose
    /// recipe reuses the BASE outfit's face is not under the alt's <see cref="Outfit.MeshPrefix"/>, so a
    /// prefix+token derivation finds nothing. Every link of recipe → address → bundle → mesh that can't
    /// resolve FAILS LOUDLY, with a <c>Note</c> naming the broken link.
    ///
    /// <para>Textures resolve renderer-first through the subject's <paramref name="scope"/>; a whole-slot
    /// miss is reported loudly and the part exports untextured. The glb is geometry-only — the rig and the
    /// textured Blender glb are rebuilt lazily by <see cref="BuildRiggedGlbs"/>.</para>
    /// </summary>
    /// <param name="onSelfWrite">Called with the full path of every <c>textures/</c> file this export is
    /// about to write, immediately before the write. A caller watching that folder for the modder's own
    /// external edits uses it to tell its writes from theirs, so the watch can stay up for the run. May be
    /// invoked from the export's thread.</param>
    public static ExportReport ExportRecipePart(string anyGamePath, GameVfs vfs, Workbench.SubjectScope scope,
        Outfit outfit, string character, RecipePart recipe, string outDir, IProgress<string>? log = null,
        CancellationToken ct = default, string? sharedRoot = null, Action<string>? onSelfWrite = null)
    {
        var report = new ExportReport { OutputDir = outDir };
        // the subject-resolution surface resolves these to nothing; this entry point takes the subject
        // directly and answers the same way (ExportBlacklist's contract: empty, no visible trace)
        if (ExportBlacklist.IsBlocked(character) || ExportBlacklist.IsBlocked(outfit.Stem)) return report;
        var reader = new BundleReader();
        var meshDir = Path.Combine(outDir, "meshes");
        var texDir = Path.Combine(sharedRoot ?? outDir, "textures");
        var subjectSlug = ModNaming.SubjectSlug(character, outfit.Stem);
        var origDir = Path.Combine(outDir, "originals");
        var texOrigDir = Path.Combine(sharedRoot ?? outDir, "originals");
        var token = recipe.Token;

        // Cached logical→plain deobfuscate. IOException (sharing violation = game running) PROPAGATES so the
        // shell's BUSY catch owns it; an absent/undecodable id → null.
        var decCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[]? Deobfuscate(string logical)
        {
            if (decCache.TryGetValue(logical, out var hit)) return hit;
            return decCache[logical] = vfs.TryDeobfuscateLogical(logical);
        }

        // recipe mesh address → owning logical bundle, or null with a named reason (the loud-fail chain).
        string? MeshBundle(string address, out string? reason)
        {
            var owner = vfs.Catalog.ResolveAddress(address);
            if (owner is null) { reason = $"no catalog entry for mesh address '{address}'"; return null; }
            if (vfs.Locate(owner) is null) { reason = $"the bundle '{owner}' (for mesh address '{address}') isn't in this install"; return null; }
            reason = null; return owner;
        }

        void FailMesh(string named)
        {
            report.Files.Add(new ExportedFile("mesh",
                recipe.SlotName.Length > 0 ? recipe.SlotName : outfit.MeshPrefix + token, "", false, named));
            log?.Report($"mesh  {token}: {named}");
        }

        // --- prefab-exact mesh resolution, loud-fail per broken link. Two backed forms: recipe (address →
        //     catalog → bundle → mesh by name), or smr-body (resolved bundle → mesh by PATH ID, since enemy
        //     bundles ship same-named copies). ---
        if (!recipe.IsRecipeBacked && !recipe.IsSmrBacked)
        {
            FailMesh($"part '{token}' carries no prefab mesh identity — neither a recipe address nor a "
                   + "serialized renderer mesh (a recipe orphan, or a prop with no skinned renderer)");
            return report;
        }
        string meshName;
        string srcBundle;
        (AssetsTools.NET.AssetTypeValueField Field, string SourceHash, bool Streamed) got;
        if (recipe.IsRecipeBacked)
        {
            meshName = recipe.SlotName;
            var owner = MeshBundle(recipe.MeshAddress, out var addrErr);
            if (owner is null) { FailMesh($"couldn't resolve part '{token}' mesh — {addrErr}"); return report; }
            srcBundle = owner;
            var meshDec0 = Deobfuscate(srcBundle);
            if (meshDec0 is null) { FailMesh($"the recipe bundle '{srcBundle}' for part '{token}' couldn't be read"); return report; }
            var g = reader.GetMeshFieldAndHash(meshDec0, meshName);
            if (g is null) { FailMesh($"mesh '{meshName}' not found in its recipe bundle '{srcBundle}'"); return report; }
            got = g.Value;
        }
        else
        {
            srcBundle = recipe.MeshBundle!;
            var meshDec0 = Deobfuscate(srcBundle);
            if (meshDec0 is null) { FailMesh($"the mesh bundle '{srcBundle}' for part '{token}' couldn't be read"); return report; }
            var g = reader.GetMeshFieldAndHashByPathId(meshDec0, recipe.MeshPathId);
            if (g is null) { FailMesh($"no mesh at path id {recipe.MeshPathId} in bundle '{srcBundle}' for part '{token}'"); return report; }
            got = g.Value;
            // The recorded name is the RENDERER SLOT's, not the mesh asset's own m_Name: on some enemy/prop
            // slots the two differ, and the ledger, the roster and the build all key a part by its slot
            // name. The selector here is the path id, so the name is not what finds the mesh.
            meshName = recipe.SlotName;
        }
        long? meshPathId = recipe.IsRecipeBacked ? null : recipe.MeshPathId;
        var field = got.Field;
        // The glb carries the part under the recorded name too: Blender's collections, the send-back match
        // and the map-origin record all join on it.
        var mesh = UnityMesh.Decode(field, meshName);
        int submeshCount = mesh.Submeshes.Count;

        // staged files commit atomically at the end (or roll back on cancel)
        var staged = new List<ExportedFile>();
        var stagedPaths = new List<string>();
        // dedupe by the bundle-scoped FILE NAME: two same-named textures from different bundles are distinct
        // files, so keying on the name alone would collapse them. pngByName is the name→path lookup the UV
        // guide needs (first-wins is fine — it only sizes the guide).
        var exportedTexPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var pngByName = new Dictionary<string, string>(StringComparer.Ordinal);
        bool cancelled = false;

        // --- textures (renderer-first PartTextureResolver, keyed by the part token) ---
        var partTex = PartTextureResolver.Resolve(scope, reader, Deobfuscate, outfit, token, submeshCount);
        // LOUD MISS: the prefab renderer bound no textures. The mesh still exports, but the part must never
        // ship untextured SILENTLY — stage a non-Ok "texture" finding (AddExport drops it, so it makes no
        // target). Materializer.CommitPart reads its Note back out as MaterializeResult.Warning.
        if (partTex.All.Count == 0)
        {
            var warn = $"part '{token}': no textures resolved from its prefab renderer — the mesh exports untextured";
            staged.Add(new ExportedFile("texture", meshName, "", false, warn));
            log?.Report($"tex   {token}: {warn}");
        }
        else if (partTex.HasFailedMaterial)
        {
            // PARTIAL miss: at least one material reference couldn't resolve, so that submesh exports
            // untextured. Same non-Ok staging as the whole-slot miss above.
            var warn = $"part '{token}': some materials couldn't be resolved from its prefab renderer — those submeshes export untextured";
            staged.Add(new ExportedFile("texture", meshName, "", false, warn));
            log?.Report($"tex   {token}: {warn}");
        }
        var users = new[] { meshName };
        Directory.CreateDirectory(texDir);
        // the two flat maps live beside the stock maps: plugging the neutral normal into a normal slot in
        // Blender IS the "blank this slot" gesture, and the preview-map sidecar identifies it by content
        PreviewMaps.WriteNeutrals(texDir);
        foreach (var t in partTex.All)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var tn = t.Name;
            // The workspace file is bundle-scoped (<name>.<bundle>.png) via the shared
            // TextureExport.BundleScopedName: two same-named textures from DIFFERENT bundles are distinct
            // game assets and must not collide on one file. A null bundle means the reference never resolved
            // — a loud per-texture failure, never a name-convention re-derivation.
            string texHash;
            try
            {
                texHash = t.Bundle ?? throw new FileNotFoundException("texture carries no pinned bundle");
            }
            catch (Exception e)
            {
                staged.Add(new("texture", tn, "", false, e.Message, t.Bundle, Users: users, Source: t.Source));
                log?.Report($"tex   {tn}: FAILED — {e.Message}");
                continue;
            }
            var fileName = TextureExport.BundleScopedName(texHash, tn, subjectSlug);
            var outFile = Path.Combine(texDir, fileName);
            if (exportedTexPath.TryGetValue(fileName, out var existing))
            {
                // Already exported THIS run — the first occurrence staged the full source_hash + meta.
                pngByName.TryAdd(tn, existing);
                staged.Add(new("texture", tn, existing, true, null, texHash, Users: users, Source: t.Source));
                continue;
            }
            if (File.Exists(outFile))
            {
                // The workspace PNG is on disk from a prior materialize but nothing staged it THIS run — read
                // the meta from the live bundle so the staged target is complete, and reuse the pristine
                // originals/ copy. IOException propagates (BUSY); any other read fault stages what we have.
                Bundles.BundleReader.TextureMeta? meta = null;
                try
                {
                    var texDec = Deobfuscate(texHash);
                    if (texDec is not null) meta = reader.GetTextureMeta(texDec, tn);
                }
                catch (IOException) { throw; }
                catch { /* couldn't read the bundle — stage what we have */ }
                var origTexReuse = Path.Combine(texOrigDir, fileName);
                exportedTexPath[fileName] = outFile;
                pngByName.TryAdd(tn, outFile);
                staged.Add(new("texture", tn, outFile, true, null, texHash,
                    File.Exists(origTexReuse) ? origTexReuse : null,
                    Users: users, TextureMeta: meta, Source: t.Source));
                continue;
            }
            try
            {
                var texDec = Deobfuscate(texHash) ?? throw new FileNotFoundException($"bundle '{texHash}' couldn't be read");
                onSelfWrite?.Invoke(outFile);
                if (!TextureExport.ExportPng(reader, texDec, tn, outFile))
                {
                    staged.Add(new("texture", tn, "", false, "not found in bundle", texHash, Users: users, Source: t.Source));
                    log?.Report($"tex   {tn}: FAILED — not found in bundle");
                    continue;
                }
                var meta = reader.GetTextureMeta(texDec, tn);
                // every exported texture keeps a pristine originals/ baseline — what revert restores and what
                // edit detection compares against — bundle-scoped to match its workspace file
                Directory.CreateDirectory(texOrigDir);
                var origTex = Path.Combine(texOrigDir, fileName);
                File.Copy(outFile, origTex, overwrite: true);
                exportedTexPath[fileName] = outFile;
                pngByName.TryAdd(tn, outFile);
                staged.Add(new("texture", tn, outFile, true, null, texHash, origTex,
                    Users: users, TextureMeta: meta, Source: t.Source));
                stagedPaths.Add(outFile);
                stagedPaths.Add(origTex);
                log?.Report($"tex   {tn} ({t.Source}) → textures/{Path.GetFileName(outFile)}");
            }
            catch (IOException) { throw; }   // sharing violation = BUSY, not a per-texture failure
            catch (Exception e)
            {
                staged.Add(new("texture", tn, "", false, e.Message));
                log?.Report($"tex   {tn}: FAILED — {e.Message}");
            }
        }

        // --- mesh glb (geometry-only) + LOD fan-out (recipe-exact siblings) + UV guide ---
        UnityMesh? stagedMesh = null; string? stagedMeshPath = null;
        if (!cancelled && !ct.IsCancellationRequested)
        {
            try
            {
                Directory.CreateDirectory(meshDir);
                var skin = MeshSkin.Decode(field);
                // Scene-rest uprighting for a mesh that ships lying down (character parts have none → null).
                // Recipe parts: the mesh's own bundle may carry its real skeleton. SMR-backed parts: the
                // skeleton lives in the subject's assembly prefab, since the SMR's ordered m_Bones point
                // positionally into that hierarchy.
                SceneRig? sceneRig = null;
                if (skin.IsSkinned)
                    sceneRig = recipe.IsRecipeBacked
                        ? SceneRig.TryRead(Deobfuscate(srcBundle)!, meshName, skin)
                        : scope.Candidates.Count > 0
                            ? SceneRig.TryReadForMeshRef(scope.Candidates[0].Dec, recipe.MeshPathId, skin)
                            : null;
                var uprighting = sceneRig?.Uprighting;
                var glbOut = Path.Combine(meshDir, Safe(token) + ".glb");
                MeshGltf.ExportGlb(mesh, glbOut, uprighting: uprighting);
                Directory.CreateDirectory(origDir);
                var origFile = Path.Combine(origDir, Safe(token) + ".glb");
                File.Copy(glbOut, origFile, overwrite: true);

                // LOD fan-out: ONLY the prefab's sibling tier slots, each resolved prefab-exact — by recipe
                // address, or by bundle+path-id on an smr-body tier. A tier that can't resolve is logged and
                // skipped, never name-guessed.
                var siblings = new List<LodSlot>();
                foreach (var sib in recipe.SiblingTiers)
                {
                    bool sibSmr = !string.IsNullOrEmpty(sib.MeshBundle) && sib.MeshPathId != 0;
                    // an empty slot name/identity is unresolvable too — logged, so no skip is ever silent
                    if (string.IsNullOrEmpty(sib.SlotName) || (string.IsNullOrEmpty(sib.MeshAddress) && !sibSmr))
                    { log?.Report($"      ({token}: LOD tier with an empty slot name/identity in the prefab — skipped)"); continue; }
                    string? sowner;
                    if (sibSmr) sowner = sib.MeshBundle;
                    else
                    {
                        sowner = MeshBundle(sib.MeshAddress, out var sreason);
                        if (sowner is null) { log?.Report($"      ({token}: LOD slot '{sib.SlotName}' not resolvable — {sreason}; skipped)"); continue; }
                    }
                    var sdec = Deobfuscate(sowner!);
                    if (sdec is null) { log?.Report($"      ({token}: LOD bundle '{sowner}' unreadable — skipped)"); continue; }
                    var sgot = sibSmr ? reader.GetMeshFieldAndHashByPathId(sdec, sib.MeshPathId)
                                      : reader.GetMeshFieldAndHash(sdec, sib.SlotName);
                    if (sgot is null) { log?.Report($"      ({token}: LOD mesh '{sib.SlotName}' not in '{sowner}' — skipped)"); continue; }
                    // recorded by SLOT name on both forms, as the representative tier is
                    siblings.Add(new LodSlot(sib.SlotName, sowner!)
                    { PathId = sibSmr ? sib.MeshPathId : null });
                }
                if (siblings.Count > 0)
                    log?.Report($"      ({token}: +{siblings.Count} LOD slot(s) → {string.Join(",", siblings.Select(s => MeshName.Lod(s.ObjectName)))})");

                staged.Add(new("mesh", meshName, glbOut, true, $"{mesh.VertexCount} verts", srcBundle, origFile,
                    siblings.Count > 0 ? siblings : null,
                    BakedRest: uprighting is { } bg ? RestBake.ToList(bg) : null,
                    PathId: meshPathId));
                stagedPaths.Add(glbOut); stagedPaths.Add(origFile);
                stagedMesh = mesh; stagedMeshPath = glbOut;
                log?.Report($"mesh  {token}: {mesh.VertexCount} verts ({submeshCount} submesh) → meshes/{Path.GetFileName(glbOut)}");

                // UV guides — per texture, beside it; shared merge artifacts, so not staged for rollback
                WriteTextureUvGuides(mesh, partTex, pngByName, log, token, onSelfWrite);
            }
            catch (IOException) { throw; }
            catch (Exception e)
            {
                staged.Add(new("mesh", meshName, "", false, e.Message));
                log?.Report($"mesh  {token}: FAILED — {e.Message}");
            }
        }
        else cancelled = true;

        // commit atomically, or trash the cancelled part's partial files
        if (cancelled || ct.IsCancellationRequested)
        {
            foreach (var p in stagedPaths) { try { File.Delete(p); } catch { /* best effort */ } }
            log?.Report(stagedPaths.Count > 0
                ? $"      (cancelled — discarded {stagedPaths.Count} partial file(s) for '{token}')"
                : $"      (cancelled before '{token}')");
            return report;
        }
        report.Files.AddRange(staged);
        if (stagedMeshPath is not null && stagedMesh is not null)
            report.OriginalMeshByPath[stagedMeshPath] = stagedMesh;
        report.CompletedParts.Add(token);
        return report;
    }

    /// <summary>
    /// Lazy open-in-Blender upgrade: rebuild the RIGGED Blender-facing glb(s) for already-exported parts —
    /// the named/posed armature + JOINTS/WEIGHTS + per-submesh preview material the Add skips, since the rig
    /// is ~the entire Add cost and is only needed when the modder actually opens Blender. Each mesh is read
    /// from the SAME bundle the Add recorded, so the geometry is byte-identical to the shipped glb; textures
    /// re-resolve from the on-disk PNGs in <paramref name="texDir"/>, keyed by
    /// <paramref name="recordedTextureBundles"/> so a game rescan can't re-point them. The bone-name table is
    /// built per rebuild from the subject's own bundles; a joint it can't name degrades to a hash-named node.
    /// An edited part takes its geometry and skin from its workspace glb but its bone NAMES from the GAME
    /// rigs of the whole subject (<see cref="EditedScenePaths"/>), so it shares the union armature's joints
    /// with the stock parts instead of hanging a second copy of every bone off the root.
    ///
    /// <para>Every armature written here spans the SUBJECT (<see cref="SubjectSkeleton"/>), not the geometry
    /// it draws: a bone another of <paramref name="parts"/> poses joins as a zero-weighted skin joint at the
    /// tail of the joint list, so it imports as a real armature bone weight can be painted onto and every
    /// part of one subject shares one rig. A part that this run writes no glb for
    /// — no <c>GlbOut</c> and no <paramref name="combinedOut"/> — is read for that skeleton alone, which
    /// costs its bundle but neither its geometry nor its textures, and it does not appear in the return.
    /// That one read is best-effort: a bundle the game holds locked drops THAT part's bones from the
    /// armature, with a line saying so, rather than failing a run whose own part reads all succeeded. Every
    /// other read still propagates its <see cref="IOException"/> as the whole-run BUSY condition.</para>
    /// Overwrites each part's <c>meshes/&lt;part&gt;.glb</c>, and — with <paramref name="combinedOut"/> set
    /// and ≥2 skinned parts — writes the union-armature combined glb. Textures, <c>originals/</c> and the
    /// project are left untouched here; the single-part caller re-copies the rigged glb over its
    /// <c>originals/</c> baseline so opening a part in Blender doesn't read as Edited. A rigid prop is
    /// skipped. Returns the part tokens that received a rig.
    /// </summary>
    /// <param name="parts">One per part: token, the bundle the mesh was read from at Add, the recorded
    /// object name (the renderer slot's), where to write the rigged per-part glb — or <c>null</c> <c>GlbOut</c> to collect it for
    /// <paramref name="combinedOut"/> WITHOUT rewriting the per-part glb, so a combined build never clobbers
    /// an edited part — the target's recorded <c>baked_rest</c>, the path-id selector, and <c>EditedGlb</c>
    /// (the part's edited workspace glb, or null to take it from the game). The bake is REPLAYED from the
    /// target, never re-derived: the rebuilt glb must sit in the same space as the Add-time
    /// workspace/originals files.</param>
    /// <param name="vanillaFallbacks">Receives the token of every part that named an <c>EditedGlb</c> the
    /// build could not assemble from and therefore took from the game instead. The caller surfaces these —
    /// the modder's own geometry is what they expected to open.</param>
    /// <param name="roster">The SUBJECT's candidacy roster, which the appended bone tail is filtered
    /// against: a bone no pool candidate of the exported part poses is refused at build time whatever weight
    /// is painted on it, so it is never offered. Spans every part of the subject, not only
    /// <paramref name="parts"/> — the union rows are the modder's PROJECT targets and a subject part they
    /// never materialized is absent from them, while the build's own roster is the whole subject. Null
    /// leaves the whole skeleton offered, the behaviour before this filter existed.</param>
    /// <param name="rosterDegraded">Receives the slot name of every <paramref name="roster"/> row whose mesh
    /// this run could not measure, plus <see cref="RosterUnfiltered"/> once per export that fell back to
    /// offering the WHOLE skeleton because candidacy was unknown for it. Diagnostic: it says the tail is
    /// narrower or wider than a fully-measured one, not whether a rerun would differ.</param>
    /// <param name="rosterUnreadable">The subset of <paramref name="rosterDegraded"/>'s rows whose BYTES
    /// were unavailable this run — a locked or missing bundle, not content that measured unmeasurable. This
    /// is the one axis of the tail the combined fingerprint cannot pin: everything else candidacy reads is a
    /// pure function of the catalog version and the workspace stamps, both already in the fingerprint, so a
    /// row that measured unmeasurable measures unmeasurable on every rerun and its tail is cacheable. A row
    /// listed HERE may read differently the moment the lock clears, so a caller caching the result must not
    /// keep it.</param>
    /// <param name="candidacyCacheFile">Where the candidacy pass may memo its per-mesh measurements
    /// (<see cref="CandidacyCache"/>), or null for no persistence: every part measured fresh, nothing left
    /// behind.
    ///
    /// <para>The memo's rows are TRUSTED as measurement — they are keyed by the content identity the game's
    /// manifest states, so an honest file can only answer for the very bytes that produced it, and a run
    /// that finds one writes what a fresh measurement would have written. Nothing re-checks a row against
    /// the mesh, though: a file tampered with locally can mis-shape the tail this export offers (narrower
    /// or wider than the part's real posed set) for as long as it sits on disk, and deleting the file is
    /// what undoes it. No BUILD is affected either way — <c>ModBuilder</c>'s roster probe measures the game
    /// afresh and never reads this file, so a mis-shaped tail can only mean bones offered in Blender that a
    /// send is then refused at, or bones not offered at all.</para></param>
    /// <param name="ct">Observed between parts, so a speculative build gives the machinery back promptly
    /// when somebody asks for it. Cancelling throws before <paramref name="combinedOut"/> is written, which
    /// is what keeps a half-built session off disk.</param>
    public static IReadOnlyList<string> BuildRiggedGlbs(string anyGamePath, GameVfs vfs,
        Outfit outfit, string character, IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut, IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
        string texDir, IProgress<string>? log = null, string? combinedOut = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? recordedTextureBundles = null,
        ICollection<string>? vanillaFallbacks = null, SubjectRoster? roster = null,
        ICollection<string>? rosterDegraded = null, string? candidacyCacheFile = null,
        CancellationToken ct = default, ICollection<string>? rosterUnreadable = null) =>
        BuildRiggedGlbsCore(anyGamePath, vfs, outfit, character, parts, texDir, log, combinedOut,
            recordedTextureBundles, vanillaFallbacks, roster, rosterDegraded,
            new CandidacyCache(candidacyCacheFile), ct, rosterUnreadable);

    /// <summary>The body of <see cref="BuildRiggedGlbs"/> on a <see cref="CandidacyCache"/> the caller
    /// owns — the seam the candidacy pass's cost is measured through, since what a run had to read and
    /// scan is otherwise invisible from outside.</summary>
    internal static IReadOnlyList<string> BuildRiggedGlbsCore(string anyGamePath, GameVfs vfs,
        Outfit outfit, string character, IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut, IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
        string texDir, IProgress<string>? log, string? combinedOut,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? recordedTextureBundles,
        ICollection<string>? vanillaFallbacks, SubjectRoster? roster,
        ICollection<string>? rosterDegraded, CandidacyCache cache, CancellationToken ct,
        ICollection<string>? rosterUnreadable = null)
    {
        // the subject-resolution surface resolves these to nothing; this entry point takes the subject
        // directly and answers the same way (ExportBlacklist's contract: empty, no visible trace)
        if (ExportBlacklist.IsBlocked(character) || ExportBlacklist.IsBlocked(outfit.Stem))
            return Array.Empty<string>();
        var subjectSlug = ModNaming.SubjectSlug(character, outfit.Stem);
        var reader = new BundleReader();
        // the glbs written here are the ones opened in Blender, so the flat maps have to be on disk beside
        // the stock maps before their sidecars are written
        PreviewMaps.WriteNeutrals(texDir);
        var decCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[]? Dec(string logical)
        {
            if (decCache.TryGetValue(logical, out var cached)) return cached;
            byte[]? bytes;
            // IOException (sharing violation = game running) PROPAGATES so the shell's BUSY catch offers
            // "close the game and retry"; swallowing it degrades a game-locked read into empty rig output
            try { bytes = vfs.TryDeobfuscateLogical(logical); }
            catch (IOException) { throw; }
            catch { bytes = null; }
            return decCache[logical] = bytes;
        }
        // A part this run writes no glb for is read for its share of the subject's skeleton and nothing else,
        // so that read is the one place the BUSY rethrow above must NOT fire: a sibling's locked bundle would
        // otherwise fail an open whose own files are all readable. It degrades instead — those bones stay off
        // this session's armature, said out loud. A part the run exports keeps the rethrow: its bundle is what
        // the caller asked for, and empty rig output for it is not an answer.
        bool SkeletonOnly(string? glbOut) => glbOut is null && combinedOut is null;
        var lockedForSkeleton = new HashSet<string>(StringComparer.Ordinal);
        byte[]? DecPart(string logical, string part, string? glbOut)
        {
            if (!SkeletonOnly(glbOut)) return Dec(logical);
            try { return Dec(logical); }
            catch (IOException)
            {
                // deliberately NOT cached as null: another part off the same bundle, one this run exports,
                // has to reach the rethrow rather than inherit this degrade
                if (lockedForSkeleton.Add(logical))
                    log?.Report($"      ({part}: the game is using its files, so its bones stay off this "
                                + "session's armature)");
                return null;
            }
        }
        // One CONTENT identity per bundle: the candidacy memo is keyed on what a bundle HOLDS, not on where
        // it came from, so a game update misses exactly the bundles it rewrote.
        //
        // The identity is read from the game's own manifest — catalog name → internalId → the stub's
        // SubHash — which is the same identity sharing reuse keys on (Workbench.BundleReads
        // .ContentHashLookup), and there is exactly one such home on purpose. Dictionary and in-memory stub
        // lookups only: NO bundle is opened to mint a key, which is the point. Hashing the deobfuscated
        // bytes would answer the same question, but only AFTER the segment read and de-XOR this memo exists
        // to avoid, so a warm open would still pay for every roster row it was about to skip.
        //
        // Null key = the manifest does not name this bundle: no memo row for it, this run or any other, and
        // NO second identity is invented to cover the gap — one identity home or none. (Such a bundle also
        // cannot be read at all, since GameVfs.Locate walks the very same two lookups, so in practice the
        // row degrades rather than measuring.) Failures are null for the same reason: a key that can't be
        // minted costs a re-measure, never an answer.
        var contentIds = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? CandidacyKey(string logical, string meshName, long pathId)
        {
            if (!cache.Enabled) return null;
            if (!contentIds.TryGetValue(logical, out var id))
            {
                try
                {
                    id = vfs.Catalog.BundleNameToInternalId.TryGetValue(logical, out var internalId)
                        && vfs.Manifest.TryLocate(internalId, out var located)
                        ? Convert.ToHexString(located.Stub.SubHash).ToLowerInvariant()
                        : null;
                }
                catch { id = null; }
                contentIds[logical] = id;
            }
            return id is null ? null : CandidacyCache.Key(id, meshName, pathId);
        }
        // The roster rows the export loop's OWN field reads can answer, joined on the slot name the way
        // ValidFor joins them, and only where the row addresses the very same mesh (same bundle, same path
        // id) the loop is about to read. Two rows on one slot name would make that join ambiguous, so a
        // roster carrying them opts out of the reuse entirely and every row goes through the gap pass, as
        // before this optimization existed.
        var rosterByMesh = new Dictionary<string, RosterPart>(StringComparer.OrdinalIgnoreCase);
        if (roster is not null)
            foreach (var r in roster.Parts)
                if (!rosterByMesh.TryAdd(r.Mesh, r)) { rosterByMesh.Clear(); break; }
        var measuredInLoop = new Dictionary<string, Migoto.PoolDerive.PartBones>(StringComparer.OrdinalIgnoreCase);

        // one scope for the whole rebuild — every part shares the subject's resolution closure and the memo
        var scope = Workbench.SubjectScope.Build(vfs.Catalog, Dec, outfit);

        // Bone-name table, per subject: fold the Transform hierarchies of the scope's candidate prefab
        // bundles (the rig lives in the assembly prefab, not the mesh bundle) plus each part's mesh source
        // bundle (self-rigged props anchor their rig beside the mesh). An unresolved joint exports as a
        // hash-named node, correctly positioned by bind pose and still paintable.
        var boneMap = new Dictionary<uint, string>();
        var boneScanned = new HashSet<string>(StringComparer.Ordinal);
        void CollectBones(string bundle, byte[]? dec)
        {
            if (dec is null || !boneScanned.Add(bundle)) return;
            try { BoneTable.CollectNodes(reader.ListTransforms(dec), boneMap); }
            catch { /* a bundle whose hierarchy won't read contributes no names; joints degrade to hashes */ }
        }
        foreach (var c in scope.Candidates) CollectBones(c.Bundle, c.Dec);
        foreach (var p in parts) CollectBones(p.SourceBundle, DecPart(p.SourceBundle, p.Part, p.GlbOut));
        var bones = BoneTable.FromMap(vfs.CatalogVersion, boneMap);

        var done = new List<string>();
        var rigged = new List<MeshGltf.RiggedPart>();
        // The combined session's included parts by SLOT NAME, filled in lockstep with `rigged` — the key the
        // candidacy roster is joined on, which a RiggedPart itself doesn't carry.
        var riggedSlots = new List<string>();
        bool anyHashNamedRig = false;   // a skinned part with NO scene rig falls back to the bone table
        // Every part's game skin against what its scene rig names and the bake it carries, in the order they
        // are read — the subject's whole answer for "which path does this bone hash take, and where does it
        // rest", which only the finished loop holds.
        var unionParts = new List<(MeshSkin Skin, IReadOnlyList<string>? BonePaths, Matrix4x4? Uprighting)>();
        // Edited parts, by their slot in `rigged`: their paths need that whole answer, so they are named
        // after the loop.
        var editedParts = new List<(int Slot, MeshSkin Skin)>();
        // Per-part rigged glbs, written after the loop: the armature each one carries spans the SUBJECT, and
        // the subject's skeleton isn't known until every part has been read.
        var pendingLone = new List<PendingLoneGlb>();
        foreach (var (part, srcBundle, meshName, glbOut, bakedRest, pathId, editedGlb) in parts)
        {
            // OUTSIDE the per-part catch, which would swallow it and carry on building.
            ct.ThrowIfCancellationRequested();
            // per-part isolation: one part failing to decode/rig must not abort the rest
            try
            {
                var dec = DecPart(srcBundle, part, glbOut);
                if (dec is null) continue;
                var field = reader.GetMeshField(dec, meshName, pathId);
                if (field is null) continue;
                // The candidacy pass's measurement for THIS slot, taken off the field already in hand — the
                // gap pass below then reads only the roster rows this loop never touched. Isolated from the
                // loop's own failure modes on purpose: a measurement that throws (a mesh whose weights can't
                // be read is the ordinary case) must leave the row UNMEASURED so the gap pass reaches it and
                // reports it degraded exactly as it does today, and must never turn into a skipped part.
                //
                // The join is by slot name the way ValidFor joins it (case-insensitively) but the mesh
                // LOOKUP that produced `field` selects m_Name case-SENSITIVELY at path id 0 — so a roster
                // row differing from the export row only in case addresses a mesh this loop did not read,
                // and claiming it here would answer a row the gap pass would have dropped. The last clause
                // closes that: at path id 0 the names must match exactly, and elsewhere the path id is the
                // selector and settles it on its own.
                if (rosterByMesh.TryGetValue(meshName, out var rosterRow)
                    && string.Equals(rosterRow.SourceBundle, srcBundle, StringComparison.Ordinal)
                    && rosterRow.PathId == pathId
                    && (pathId != 0 || string.Equals(rosterRow.Mesh, meshName, StringComparison.Ordinal))
                    && !measuredInLoop.ContainsKey(meshName))
                {
                    try
                    {
                        // Keyed off the ROSTER ROW's own triple, which is what the gap pass would key on:
                        // one key per asset whichever route mints it, so the two can't memo the same mesh
                        // twice under two names.
                        var key = CandidacyKey(rosterRow.SourceBundle, rosterRow.Mesh, rosterRow.PathId);
                        measuredInLoop[meshName] = CandidacyRow(rosterRow, roster!.Scheme,
                            cache.TryGet(key) ?? cache.Measure(key, field));
                    }
                    catch { /* unmeasured here ⇒ the gap pass measures it, degraded reporting and all */ }
                }
                var skin = MeshSkin.Decode(field);
                // A recorded rest that is not an axis-aligned rotation cannot be un-baked by transpose, so
                // the export skips it and the part lands in bind space rather than a skewed one.
                var recordedRest = RestBake.FromList(bakedRest, out bool restRefused);
                // scene rig for NAMES/parenting; the bake replays the target's record, so this rebuild
                // lands in the same space the Add put the workspace in. The mesh-bundle read comes first
                // (classes whose own bundle carries the scene), selected by the path id where there is
                // one — the recorded name is the slot's and need not name the mesh object. smr-body
                // parts fall back to the assembly prefab, keyed by the slot's mesh reference. Read ONCE
                // per part: an edited part needs it too, for the map and for its connectors.
                SceneRig? sceneRig = null;
                if (skin is { IsSkinned: true })
                {
                    sceneRig = SceneRig.TryRead(dec, meshName, skin, pathId)
                        ?? (pathId != 0 && scope.Candidates.Count > 0
                            ? SceneRig.TryReadForMeshRef(scope.Candidates[0].Dec, pathId, skin)
                            : null);
                    // the rig's paths are in GAME bone order, so they pair with this part's game skin
                    unionParts.Add((skin, sceneRig?.BonePaths, recordedRest));
                }
                // A part this run writes no glb for has already given what it was read for — its share of
                // the subject's skeleton. Decoding its geometry and resolving its textures would buy
                // nothing, so a rig-only part costs one bundle read rather than a whole part export.
                if (glbOut is null && combinedOut is null) continue;
                // A recorded rest that is not an axis-aligned rotation cannot be un-baked by transpose, so
                // the export skips it and the part lands in bind space rather than a skewed one.
                if (restRefused)
                    log?.Report($"      ({part}: its recorded rest pose is not an axis-aligned rotation; the export skips it)");
                // Named by the RECORDED name, which is the renderer slot's; on some enemy/prop slots the
                // mesh asset's own m_Name differs, and the send-back joins its parts to the ledger by the
                // name the glb carries.
                var mesh = UnityMesh.Decode(field, meshName);
                var partTex = PartTextureResolver.Resolve(scope, reader, Dec, outfit, part, mesh.Submeshes.Count);
                // the renderer bound no textures — the preview rebuilds untextured, but never silently
                if (partTex.All.Count == 0)
                    log?.Report($"      ({part}: no textures resolved from its prefab renderer — the preview is untextured)");
                // resolve each renderer texture to its bundle-scoped workspace PNG; a resolved-but-absent map
                // is reported per texture, never silently dropped
                var (baseColorPng, normalPng, perSubmesh) = ResolvePartPngs(texDir, subjectSlug, partTex, part, log, recordedTextureBundles);
                if (skin is { IsSkinned: true })
                {
                    var uprighting = recordedRest;
                    // The modder's own geometry wins for an edited part: its workspace glb holds the authored
                    // mesh AND skin, so the session opens on what they last sent rather than the game copy
                    // their next send would overwrite. It already sits in the space the Add put it in, so it
                    // combines with no further uprighting. Maps still come from the workspace PNGs — a
                    // workspace glb carries none.
                    if (editedGlb is not null && glbOut is null)
                    {
                        // A workspace glb that won't parse degrades to the game copy rather than dropping the
                        // part — caught HERE, not by the per-part isolation, which would mistake a locked
                        // workspace file for a game-locked BUSY condition.
                        (UnityMesh Mesh, MeshSkin Skin)? edited;
                        try { edited = MeshGltf.ReadRiggedGlb(editedGlb); }
                        catch { edited = null; }
                        // That read hands back the file's WHOLE joint list — this session's union armature,
                        // subject tail and all — so the skin has to be reduced to the bones the modder's
                        // geometry rides before it joins the others. Unreduced, the tail's stale worlds win
                        // the union's first-claim for bones LATER parts pose, and CombinedExtraBones sees a
                        // `posed` set spanning the skeleton. A painted tail bone carries weight, so it
                        // survives and stays this part's own joint.
                        if (edited is { } read) edited = MeshSkin.WeightedOnly(read.Mesh, read.Skin);
                        if (edited is { } e)
                        {
                            editedParts.Add((rigged.Count, e.Skin));
                            rigged.Add(new MeshGltf.RiggedPart(e.Mesh, e.Skin, baseColorPng, normalPng,
                                ConnectorRests: Composed(sceneRig?.ConnectorRests, uprighting),
                                PerSubmesh: perSubmesh));
                            riggedSlots.Add(meshName);
                            done.Add(part);
                            continue;
                        }
                        // never silent: the game copy opens instead, and the caller says which part
                        vanillaFallbacks?.Add(part);
                        log?.Report($"      ({part}: its edited file couldn't be read; the game version opens instead)");
                    }
                    if (sceneRig is null && bones.Count == 0) anyHashNamedRig = true;
                    // null glbOut ⇒ collect for the combined glb only, don't rewrite the per-part glb. The
                    // lone glb is a REPLACEABLE part's round-trip file, so it takes no prefab placement:
                    // mesh and joints land together at the part's own origin, as the combined session puts a
                    // replaceable part (see CombinedPose).
                    if (glbOut is not null)
                        pendingLone.Add(new PendingLoneGlb(part, meshName, glbOut, mesh, skin, baseColorPng,
                            normalPng, perSubmesh, sceneRig?.BonePaths, uprighting, sceneRig?.ConnectorRests));
                    var (contextPose, connectors) = CombinedPose(sceneRig, uprighting,
                        // the gated read is the expensive half, so it runs only where the answer can matter
                        () => Workbench.PartSkinGate.Blocked(Dec, srcBundle, meshName, pathId, reader) is null);
                    rigged.Add(new MeshGltf.RiggedPart(mesh, skin, baseColorPng, normalPng,
                        sceneRig?.BonePaths, uprighting, connectors,
                        PerSubmesh: perSubmesh,
                        ContextPose: contextPose));   // for the union combined
                    riggedSlots.Add(meshName);
                    done.Add(part);
                }
                else if (glbOut is not null)   // rigid prop: no rig, but still upgrade its bare Add glb to textured
                {
                    MeshGltf.ExportGlb(mesh, glbOut, baseColorPng, normalPng, perSubmesh, recordedRest);
                    done.Add(part);
                }
            }
            // A game-locked read is a WHOLE-run BUSY condition, not a per-part failure: game-file-locked must
            // never degrade into empty output. Genuine per-part decode faults stay isolated below.
            catch (IOException) { throw; }
            catch { /* skip this part, keep building the others */ }
        }
        // An edited part is named from the subject's whole rig answer, exactly as a stock part is named from
        // its own, so the two land on the SAME union joints for a shared bone.
        var partRigs = unionParts.Select(p => (p.Skin, p.BonePaths)).ToList();
        foreach (var (slot, editedSkin) in editedParts)
        {
            var paths = EditedScenePaths(editedSkin, partRigs);
            if (bones.Count == 0 && Array.IndexOf(paths, null) >= 0) anyHashNamedRig = true;
            rigged[slot] = rigged[slot] with { ScenePaths = paths };
        }
        // The hash-name warning fires only when a skinned part ACTUALLY degraded: no scene rig to name it AND
        // an empty bone table. An enemy subject legitimately has an empty root/Root_M-anchored table
        // (Bip001/Bone001 rigs) while every part's scene rig supplies real names — not a degrade.
        if (rigged.Count > 0 && anyHashNamedRig)
            log?.Report("      (no bone names resolved for part(s) of this subject; those rig joints use hash names)");

        var skeleton = SubjectSkeleton(unionParts, bones.Path, out var disagreeing);
        foreach (var line in DisagreementLines(disagreeing)) log?.Report(line);

        // The subject's candidacy roster, measured the way the BUILD measures it (ModBuilder's RosterProbe):
        // bone table + narrow layout + presence + posed bones + the prefab's shadow and visibility flags. It
        // is what decides which of the subject's bones a tail may offer, and it is read for the whole subject
        // — the rows above cover only the parts this project materialized.
        var candidacy = CandidacyRoster(roster, reader, Dec, measuredInLoop, cache, CandidacyKey,
            rosterDegraded, rosterUnreadable);
        // The roster rows that produced no candidacy, as the build's own held-back list reads them: a
        // wardrobe slot with an unmeasured part of its own certifies no coverage, and the wardrobe standing
        // is what says which slot that part would have belonged to.
        var unmeasured = candidacy is null || roster is null
            ? Array.Empty<Migoto.PoolDerive.MissingPart>()
            : roster.Parts
                .Where(r => !candidacy.Any(c => string.Equals(c.Mesh, r.Mesh, StringComparison.OrdinalIgnoreCase)))
                .Select(r => new Migoto.PoolDerive.MissingPart(r.Mesh, RosterUnmeasured, null,
                    Migoto.PartPresence.Classify(r.Token, roster.Scheme)))
                .ToArray();
        // Every measurement this run made is in; the memo is published once, best-effort, and nothing after
        // this point depends on it.
        cache.Flush();
        // Filtering is SILENT by design: a bone left off was guaranteed a refusal at build time, so naming it
        // would be a line per bone about work nobody asked for. Null out means UNFILTERED for that part,
        // never "nothing is valid": a part the roster doesn't carry has unknown candidacy, and
        // PoolDerive.PoolCandidates would read an unlisted target as an unconditional non-target — excluding
        // its narrow, off-presence, shadow-off and withheld siblings all at once AND losing the part's own
        // posed set, which is a genuine under-offer rather than the guaranteed-refusal omission this filter
        // is for. The fallback is recorded, because the tail it writes isn't the filtered one.
        HashSet<uint>? ValidFor(string slot)
        {
            if (candidacy is null) return null;
            if (!candidacy.Any(p => string.Equals(p.Mesh, slot, StringComparison.OrdinalIgnoreCase)))
            {
                rosterDegraded?.Add(RosterUnfiltered);
                return null;
            }
            return ValidTailBones(candidacy, slot, roster?.Scheme, unmeasured,
                roster?.PartsPoolAlone ?? false);
        }

        foreach (var w in pendingLone)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                MeshGltf.ExportRiggedGlb(w.Mesh, w.Skin, bones.Path, w.GlbOut, w.BaseColorPng, w.NormalPng,
                    w.PerSubmesh, w.ScenePaths, w.Uprighting, w.ConnectorRests,
                    ExtraBones(skeleton, w.Skin.BoneHashes, w.Uprighting, ValidFor(w.MeshName)),
                    m => log?.Report($"      ({w.Part}: {m})"));
            }
            catch (IOException) { throw; }
            catch (Exception e)
            {
                // the part's own glb is what a lone session opens and what its compile round trips, so a
                // write that didn't land must not be reported as rigged
                done.Remove(w.Part);
                log?.Report($"      ({w.Part}: its rig couldn't be written — {e.Message})");
            }
        }
        ct.ThrowIfCancellationRequested();
        if (combinedOut is not null && rigged.Count >= 2)
        {
            // The UNION over the included parts, not any one part's: one shared armature, so there is no
            // per-part tail to give. A lone session is where a part gets its exact set. One included slot
            // whose candidacy is UNKNOWN takes the whole union to null: the parts share an armature, so
            // narrowing it by the slots that did read would hide bones the unknown part may well have been
            // able to paint, and unknown means offer everything.
            HashSet<uint>? combinedValid = null;
            if (candidacy is not null)
            {
                var union = new HashSet<uint>();
                foreach (var slot in riggedSlots)
                {
                    var v = ValidFor(slot);   // records its own fallback
                    if (v is null) { union = null; break; }
                    union.UnionWith(v);
                }
                combinedValid = union;
            }
            MeshGltf.ExportCombinedRiggedGlb(rigged, bones.Path, combinedOut,
                CombinedExtraBones(skeleton, rigged, combinedValid), m => log?.Report($"      ({m})"));
        }
        return done;
    }

    /// <summary>The subject's parts as <see cref="Migoto.PoolDerive.PoolCandidates"/> reads them, assembled
    /// the way the build's own roster probe assembles them: <c>m_BoneNameHashes</c> for the table, the skin
    /// layout for narrowness, the part token against the wardrobe scheme for presence, the measured weighted
    /// hashes for what it POSES, and the prefab's own shadow/visibility flags. Null roster in, null out —
    /// candidacy unknown, and nothing is filtered.
    ///
    /// <para>A part whose mesh won't read, or whose weights can't be measured, is left OUT of the roster
    /// entirely. That mirrors the build, which holds such a part back from pool derivation: offering it with
    /// its bone TABLE standing in for a posed set would put back exactly the bones the posed gate exists to
    /// refuse. Its exclusively-posed bones therefore reach no tail.</para>
    ///
    /// <para>EVERY row failing is a different answer from every row saying "poses nothing": the subject was
    /// not measured at all, so candidacy is unknown and this returns null — a whole skeleton offered, as with
    /// no roster. An empty list here would instead filter the entire tail away, silently and for reasons
    /// (a bogus bundle, a game-locked read) that say nothing about what a build would accept.</para>
    ///
    /// <para><paramref name="degraded"/> receives the slot name of every row dropped.
    /// <paramref name="unreadable"/> receives the subset whose BYTES were unavailable this run — the drops a
    /// rerun might not repeat. A row whose bundle read fine but whose mesh is absent or measures
    /// unmeasurable is a fact of the content: the same catalog serves the same bytes to every rerun, so it
    /// lands in <paramref name="degraded"/> alone, and a caller deciding whether this run's tail is
    /// repeatable reads <paramref name="unreadable"/>.</para>
    ///
    /// <para>This is the GAP pass: <paramref name="measured"/> holds the rows the export's own loop already
    /// answered off the mesh fields it fetched for the export itself, and they are taken as they stand — the
    /// rules that produced them are the ones below, through the one shared <see cref="CandidacyRow"/>. Only
    /// the rows left over cost anything here, and a row the memo can answer costs NOTHING: its key is minted
    /// from the manifest, so the memo is asked before the bundle is opened and a hit skips the bundle read
    /// and the field fetch alike. The list comes back in ROSTER order whichever route filled each row, which
    /// is the order <see cref="Migoto.PoolDerive.PartBones"/> must be supplied in.</para></summary>
    private static List<Migoto.PoolDerive.PartBones>? CandidacyRoster(SubjectRoster? roster,
        Bundles.BundleReader reader, Func<string, byte[]?> dec,
        IReadOnlyDictionary<string, Migoto.PoolDerive.PartBones> measured, CandidacyCache cache,
        Func<string, string, long, string?> keyOf, ICollection<string>? degraded = null,
        ICollection<string>? unreadable = null)
    {
        if (roster is not { Parts.Count: > 0 }) return null;
        var bones = new List<Migoto.PoolDerive.PartBones>();
        foreach (var r in roster.Parts)
        {
            if (measured.TryGetValue(r.Mesh, out var already)) { bones.Add(already); continue; }
            // No failure fails the run, IOException included: this read is a supplement to the export's
            // own, which already ran and already raised the game-locked BUSY condition for the parts this
            // run writes. A sibling nobody asked to export must not turn a readable session into a failed
            // one — it drops out of the roster, which only narrows what is offered.
            // The memo is consulted FIRST, before the bundle is touched: the key comes off the manifest,
            // so a hit costs no segment read and no de-XOR — which is the whole saving, the read being
            // far dearer than the scan it also spares. A hit is proof the very same bundle content held
            // this mesh and measured, so none of the drops below can be reached for it.
            AssetsTools.NET.AssetTypeValueField? field;
            string? key;
            try
            {
                key = keyOf(r.SourceBundle, r.Mesh, r.PathId);
                if (cache.TryGet(key) is { } hit) { bones.Add(CandidacyRow(r, roster.Scheme, hit)); continue; }
                cache.BundleReads++;
                var d = dec(r.SourceBundle);
                // bytes unavailable RIGHT NOW — a lock or a missing file, which a rerun may not repeat
                if (d is null) { degraded?.Add(r.Mesh); unreadable?.Add(r.Mesh); continue; }
                cache.MeshReads++;
                field = reader.GetMeshField(d, r.Mesh, r.PathId);
            }
            // conservatively the same class: whatever threw between the manifest and the mesh bytes, this
            // run cannot say the content itself refused
            catch { degraded?.Add(r.Mesh); unreadable?.Add(r.Mesh); continue; }
            // From here the bundle's bytes WERE served, so the verdict is the content's own: a mesh the
            // bundle doesn't hold, or one whose weights refuse measurement, refuses identically on every
            // rerun of the same catalog — degraded, but repeatable.
            if (field is null) { degraded?.Add(r.Mesh); continue; }
            try { bones.Add(CandidacyRow(r, roster.Scheme, cache.Measure(key, field))); }
            catch { degraded?.Add(r.Mesh); /* unmeasurable content ⇒ not a candidate, exactly as the build holds it back */ }
        }
        // Not one row measured ⇒ nothing was learned about this subject, which is not the same as learning
        // that its parts pose nothing. Unknown offers everything.
        if (bones.Count == 0) { degraded?.Add(RosterUnfiltered); return null; }
        return bones;
    }

    /// <summary>One roster row's candidacy: the mesh-derived triple joined to the half that comes off the
    /// prefab and the wardrobe every run (presence, shadow, visibility — none of it cacheable, none of it in
    /// the mesh's bytes). The single place the two halves meet, so the export loop's rows and the gap pass's
    /// rows are assembled by exactly the same rules.</summary>
    private static Migoto.PoolDerive.PartBones CandidacyRow(RosterPart r,
        IReadOnlyList<Tables.PartScheme.Slot>? scheme, MeshCandidacy m) =>
        new(r.Mesh, m.Table,
            Narrow: m.Narrow,
            Presence: Migoto.PartPresence.Classify(r.Token, scheme),
            PosedBones: m.Posed,
            CastsShadows: r.CastsShadows,
            Visibility: r.Visibility);

    /// <summary>One per-part rigged glb waiting on the subject's skeleton (see
    /// <see cref="BuildRiggedGlbs"/>). <see cref="MeshName"/> is the recorded slot name — the key the
    /// candidacy roster is joined on, which <see cref="Part"/> (a token on one route and a slot name on the
    /// other) is not.</summary>
    private readonly record struct PendingLoneGlb(string Part, string MeshName, string GlbOut, UnityMesh Mesh,
        MeshSkin Skin, string? BaseColorPng, string? NormalPng,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? PerSubmesh,
        IReadOnlyList<string>? ScenePaths, Matrix4x4? Uprighting,
        IReadOnlyDictionary<string, Matrix4x4>? ConnectorRests);

    /// <summary>One bone of the subject's skeleton: where it hangs, its rest world in BIND space
    /// (<c>inverse(bindPose)</c>, before any bake), and the uprighting the part it was read from
    /// carries.</summary>
    internal readonly record struct SubjectBone(uint Hash, string Path, Matrix4x4 BindRest, Matrix4x4? Uprighting);

    /// <summary>How far apart two parts may bind one bone, in TRANSLATION, and still be read as agreeing
    /// about where it stands (see <see cref="SubjectSkeleton"/>). Deliberately not
    /// <see cref="RestBake.TranslationTol"/>: that one answers "is this translation small enough to drop
    /// from a bake", so a whole centimetre passes it — as a placement gate the same number would call two
    /// rests a centimetre apart the same bone and stand an armature stick between them. This answers "do
    /// these two parts place this bone in the same spot", where a centimetre is plainly visible. 1e-4 sits
    /// well above the ~1e-6 noise of inverting a bind pose and well below any real placement difference.
    /// Rotation stays on <see cref="RestBake.RotationTol"/>, already the tight half of that split.</summary>
    private const float PlacementAgreementTol = 1e-4f;

    /// <summary>How many disagreeing bones <see cref="DisagreementLines"/> names before it counts the
    /// rest.</summary>
    private const int NamedDisagreements = 3;

    /// <summary>What a build says about the bones <see cref="SubjectSkeleton"/> dropped. Each line goes to
    /// the status bar, and a rig whose parts are systematically offset disagrees about EVERY bone it has —
    /// so three are named and the remainder is a count. Naming none would hide a two-bone problem; naming
    /// all of them buries every other line of the build.</summary>
    internal static IEnumerable<string> DisagreementLines(IReadOnlyList<string> disagreeing)
    {
        for (int i = 0; i < disagreeing.Count && i < NamedDisagreements; i++)
            yield return $"      (bone {disagreeing[i]}: this subject's parts bind it in different places, so "
                         + "it stays off the shared armature)";
        if (disagreeing.Count > NamedDisagreements)
            yield return $"      (…and {disagreeing.Count - NamedDisagreements} more bones bind in different "
                         + "places, all off the shared armature)";
    }

    /// <summary>
    /// The whole subject's skeleton in the order its parts were read: every bone any part skins, named the
    /// way that part's export names it (its scene rig first, then the bone table, then a flat
    /// <c>bone_&lt;hash8&gt;</c>) so one bone reaches one armature node however many parts pose it.
    ///
    /// <para>Bind poses are the placement source, and the first part to name a bone fixes both its path and
    /// its rest — but only while the subject AGREES about it. A bone two parts bind in different places has
    /// no one rest to stand at, so it is dropped from the skeleton entirely and named in
    /// <paramref name="disagreeing"/>: it still poses the parts that own it, it just never joins another
    /// part's armature. An armature stick in the wrong place is worse than an absent one.</para>
    /// </summary>
    internal static IReadOnlyList<SubjectBone> SubjectSkeleton(
        IReadOnlyList<(MeshSkin Skin, IReadOnlyList<string>? BonePaths, Matrix4x4? Uprighting)> parts,
        Func<uint, string?> resolveBone, out IReadOnlyList<string> disagreeing)
    {
        var byHash = new Dictionary<uint, int>();       // hash → its slot in `bones`
        var bones = new List<SubjectBone>();
        var dropped = new HashSet<uint>();
        var names = new List<string>();
        foreach (var (skin, bonePaths, uprighting) in parts)
            // a skin whose bind poses don't reach its bone list places nothing past that point, and this runs
            // outside the per-part isolation the reads have
            for (int i = 0; i < skin.BoneCount && i < skin.BindPoses.Count; i++)
            {
                uint hash = skin.BoneHashes[i];
                if (dropped.Contains(hash)) continue;
                if (!Matrix4x4.Invert(skin.BindPoses[i], out var rest)) continue;   // no placement, no bone
                if (byHash.TryGetValue(hash, out var at))
                {
                    if (RestBake.RotationDiff(rest, bones[at].BindRest) <= RestBake.RotationTol
                        && RestBake.TranslationDiff(rest, bones[at].BindRest) <= PlacementAgreementTol)
                        continue;
                    names.Add(bones[at].Path);
                    bones.RemoveAt(at);
                    byHash.Remove(hash);
                    foreach (var h in byHash.Keys.ToList()) if (byHash[h] > at) byHash[h]--;
                    dropped.Add(hash);
                    continue;
                }
                string path = (bonePaths is not null && i < bonePaths.Count ? bonePaths[i] : null)
                              ?? resolveBone(hash) ?? $"bone_{hash:x8}";
                byHash[hash] = bones.Count;
                bones.Add(new SubjectBone(hash, path, rest, uprighting));
            }
        disagreeing = names;
        return bones;
    }

    /// <summary>The subject's bones a LONE part's armature carries on top of its own: everything the part
    /// doesn't skin, placed the way that export places its own joints — bind rest composed with the
    /// uprighting this glb bakes, so rig and geometry stand in one space.
    ///
    /// <para><paramref name="valid"/> is the bones a build would let weight be painted onto for THIS part
    /// (<see cref="ValidTailBones"/>); anything outside it is refused at build time whatever the modder
    /// paints, so it is left off rather than offered. Null = candidacy unknown, and the whole skeleton is
    /// offered as before. The part's OWN joints are untouched either way — this selects only the appended
    /// tail, so a send that painted nothing still re-splits onto the same joint indices. An ANCESTOR of an
    /// offered bone comes back whatever the filter says (see <see cref="OfferedTail"/>).</para></summary>
    internal static IReadOnlyList<MeshGltf.ExtraBone> ExtraBones(IReadOnlyList<SubjectBone> skeleton,
        IReadOnlyList<uint> own, Matrix4x4? uprighting, IReadOnlySet<uint>? valid = null)
    {
        var offered = OfferedTail(skeleton, new HashSet<uint>(own), valid);
        var extras = new List<MeshGltf.ExtraBone>();
        foreach (var b in skeleton)
            if (offered.Contains(b.Hash))
                extras.Add(new MeshGltf.ExtraBone(b.Hash, b.Path,
                    uprighting is { } g ? b.BindRest * g : b.BindRest));
        return extras;
    }

    /// <summary>
    /// Which of <paramref name="skeleton"/>'s bones an appended tail offers: everything the geometry doesn't
    /// already pose (<paramref name="posed"/>) that <paramref name="valid"/> admits — plus every skeleton
    /// ANCESTOR of a bone that survived, whatever <paramref name="valid"/> says about the ancestor itself.
    /// Returned as a hash set; the callers walk the skeleton to keep its order.
    ///
    /// <para>The ancestor clause is not a widening of the rule, it is what keeps the omission HONEST.
    /// <c>MeshGltf</c>'s armature build registers every '/'-split prefix of an offered bone's path as a node,
    /// so dropping an ancestor doesn't remove it from the file — it leaves it there stripped of its hash
    /// suffix and parked at an identity world. Blender imports a joint's node ancestors as bones, so the
    /// modder still gets something paintable, and paint on a hash-less joint is DISCARDED on the way back in
    /// (its influences are dropped and the vertex renormalised) instead of meeting the build's posed gate and
    /// being refused out loud. Restoring the ancestor as a proper hash-named joint puts that refusal back:
    /// pre-filter behaviour for a bone the filter has no way to hide anyway.</para>
    /// </summary>
    private static HashSet<uint> OfferedTail(IReadOnlyList<SubjectBone> skeleton, HashSet<uint> posed,
        IReadOnlySet<uint>? valid)
    {
        var offered = new HashSet<uint>();
        foreach (var b in skeleton)
            if (!posed.Contains(b.Hash) && (valid is null || valid.Contains(b.Hash)))
                offered.Add(b.Hash);
        if (valid is null) return offered;   // nothing was dropped, so nothing can need restoring

        // Every path the armature will carry a hash-named joint for: the tail so far, and the geometry's own
        // bones as the skeleton names them. An ancestor of ANY of them is a node this glb writes regardless.
        var kept = new List<string>();
        foreach (var b in skeleton)
            if (offered.Contains(b.Hash) || posed.Contains(b.Hash)) kept.Add(b.Path);
        // One pass suffices: an ancestor of an ancestor of a kept bone is itself a prefix of that kept bone.
        foreach (var b in skeleton)
        {
            if (offered.Contains(b.Hash) || posed.Contains(b.Hash) || b.Path.Length == 0) continue;
            var prefix = b.Path + "/";
            foreach (var k in kept)
                if (k.StartsWith(prefix, StringComparison.Ordinal)) { offered.Add(b.Hash); break; }
        }
        return offered;
    }

    /// <summary>The combined session's twin of <see cref="ExtraBones"/>: a bone no part in the session poses
    /// stands where the part it was READ from would have put it, since this glb bakes no one uprighting of
    /// its own — each part carries its own.
    ///
    /// <para>"Poses" is read off each part's skin, so every part handed here must already list only the
    /// bones its geometry rides (<see cref="MeshSkin.WeightedOnly"/> reduces an edited part's re-read skin
    /// to that). A part whose skin still spans the subject leaves this with nothing to add, and the bones
    /// <see cref="SubjectSkeleton"/> deliberately dropped come back through that part's own stale
    /// worlds.</para>
    ///
    /// <para><paramref name="valid"/> is the UNION of the included parts' valid tail sets
    /// (<see cref="ValidTailBones"/>), not any one part's: this glb ships ONE shared armature every part
    /// binds to, so a per-part tail is structurally impossible here. A bone valid for one included part and
    /// not another is therefore offered — a lone session is where a part gets its exact set. Null =
    /// candidacy unknown, and the whole skeleton is offered as before. An ANCESTOR of an offered bone comes
    /// back whatever the filter says (see <see cref="OfferedTail"/>).</para></summary>
    internal static IReadOnlyList<MeshGltf.ExtraBone> CombinedExtraBones(IReadOnlyList<SubjectBone> skeleton,
        IReadOnlyList<MeshGltf.RiggedPart> parts, IReadOnlySet<uint>? valid = null)
    {
        var posed = new HashSet<uint>();
        foreach (var p in parts)
            foreach (var h in p.Skin.BoneHashes) posed.Add(h);
        var offered = OfferedTail(skeleton, posed, valid);
        var extras = new List<MeshGltf.ExtraBone>();
        foreach (var b in skeleton)
            if (offered.Contains(b.Hash))
                extras.Add(new MeshGltf.ExtraBone(b.Hash, b.Path,
                    b.Uprighting is { } g ? b.BindRest * g : b.BindRest));
        return extras;
    }

    /// <summary>The bones a build would let weight be painted onto for <paramref name="part"/>: the union of
    /// what its POOL CANDIDATES pose. Candidacy is <see cref="Migoto.PoolDerive.PoolCandidates"/> and nothing
    /// else — the one seam the narrow/presence/shadow/visibility rules live at — and "poses" is nonzero
    /// summed vertex weight, never mere bone-table membership, since a bone every candidate merely TABLES is
    /// exactly what the build's posed gate refuses. Capture-only tier carriers are ranked out of the same
    /// candidate set, so this union is the complete valid set with no carrier logic of its own.
    ///
    /// <para><paramref name="roster"/> is the whole subject's candidacy roster in roster order; a part it
    /// left out (unreadable mesh, unmeasurable weights) counts as posing nothing, mirroring the build, which
    /// holds such a part back from pool derivation altogether. <paramref name="part"/> is a slot name,
    /// matched the way <see cref="Migoto.PoolDerive.PoolCandidates"/> matches its target.</para>
    ///
    /// <para>Plus what the outfit's own alternation covers: a bone with an on-screen poser in every
    /// variant×context state the target displays in is posed whatever the player wears, so a build accepts
    /// paint on it even though no single such poser is a candidate
    /// (<see cref="Migoto.PoolDerive.VariantGroups"/>). <paramref name="schemeSlots"/> is the outfit's
    /// wardrobe and <paramref name="heldBack"/> the roster rows this run could not measure; without the
    /// wardrobe only the target's own arm can certify, which is the under-offering direction the build's
    /// own posed gate backstops.</para></summary>
    internal static HashSet<uint> ValidTailBones(IReadOnlyList<Migoto.PoolDerive.PartBones> roster, string part,
        IReadOnlyList<Tables.PartScheme.Slot>? schemeSlots = null,
        IReadOnlyList<Migoto.PoolDerive.MissingPart>? heldBack = null,
        bool partsPoolAlone = false)
    {
        var (candidates, _) = Migoto.PoolDerive.PoolCandidates(roster, part, partsPoolAlone);
        var valid = new HashSet<uint>();
        foreach (var c in candidates) valid.UnionWith(c.Posed);
        foreach (var g in Migoto.PoolDerive.VariantGroups(roster, schemeSlots,
                     heldBack ?? Array.Empty<Migoto.PoolDerive.MissingPart>(), candidates, part,
                     partsPoolAlone))
            valid.UnionWith(g.GroupBones);
        return valid;
    }

    /// <summary>
    /// Where a part sits in the combined session: the per-bone scene rest worlds to pose it at (null = its
    /// own bind rest) and the connector rests that go with them.
    ///
    /// <para>A part <paramref name="replaceable"/> answers false for — one no session can send back, because
    /// its Replace is gated — is CONTEXT in every session, so geometry and joints both export at the prefab's
    /// scene rests and a weapon sits at its mount. A REPLACEABLE part's bytes have to stay raw bind space for
    /// its compile to round-trip, so it takes NO pose at all: mesh and armature sit together at its own
    /// origin, whatever offset the prefab mounts it by. <paramref name="uprighting"/> excludes both — a baked
    /// part's geometry already carries its rest.</para>
    ///
    /// <para><paramref name="replaceable"/> is a delegate: answering it costs a bundle read, and only a part
    /// with a scene rig and no bake can reach a different answer.</para>
    /// </summary>
    internal static (IReadOnlyList<Matrix4x4>? ContextPose, IReadOnlyDictionary<string, Matrix4x4>? Connectors)
        CombinedPose(SceneRig? sceneRig, Matrix4x4? uprighting, Func<bool> replaceable) =>
        uprighting is null && sceneRig?.BoneRestWorlds is { } restWorlds && !replaceable()
            // recover the connectors' true SCENE worlds for a posed part: their recorded rests are
            // bind-normalized by inverse(measured G), so composing G back undoes it
            ? (restWorlds, Composed(sceneRig.ConnectorRests, sceneRig.MeasuredRest))
            : (null, sceneRig?.ConnectorRests);

    /// <summary>Connector rests arrive in BIND space and the export composes them with the uprighting it
    /// applies. A part that takes NO uprighting — its geometry already carries the bake — needs that
    /// composition done here, or its connectors land a bake behind the same connectors on a stock
    /// part.</summary>
    private static IReadOnlyDictionary<string, Matrix4x4>? Composed(
        IReadOnlyDictionary<string, Matrix4x4>? rests, Matrix4x4? g)
    {
        if (rests is null || g is not { } m) return rests;
        var composed = new Dictionary<string, Matrix4x4>(rests.Count, StringComparer.Ordinal);
        foreach (var (prefix, rest) in rests) composed[prefix] = rest * m;
        return composed;
    }

    /// <summary>
    /// Scene-bone paths for an EDITED part's workspace skin, in its bone order — named from what the
    /// subject's parts collectively say, each part's <see cref="SceneRig.BonePaths"/> paired with that
    /// part's GAME skin in bone order (first rig to claim a hash wins).
    ///
    /// <para>A workspace glb carries the whole session's union armature, not just the part's own bones, so
    /// the part's own rig alone cannot name it — a bone another part owns is named by that part. A hash no
    /// rig names stays null: the export then falls back to the bone table, and to a flat
    /// <c>bone_&lt;hash8&gt;</c> node only when the table is empty too. Naming the same hash the same way
    /// for every part is what keeps one bone to one joint in the union armature.</para>
    /// </summary>
    internal static string?[] EditedScenePaths(MeshSkin editedSkin,
        IReadOnlyList<(MeshSkin Skin, IReadOnlyList<string>? BonePaths)> partRigs)
    {
        var byHash = new Dictionary<uint, string>();
        foreach (var (skin, bonePaths) in partRigs)
        {
            if (bonePaths is null) continue;
            for (int i = 0; i < skin.BoneCount && i < bonePaths.Count; i++)
                byHash.TryAdd(skin.BoneHashes[i], bonePaths[i]);
        }
        var named = new string?[editedSkin.BoneCount];
        for (int i = 0; i < editedSkin.BoneCount; i++)
            named[i] = byHash.GetValueOrDefault(editedSkin.BoneHashes[i]);
        return named;
    }

    /// <summary>The UV-guide file for a workspace texture PNG: <c>&lt;name&gt;.&lt;bundle&gt;.uvguide.png</c>
    /// beside it. The one naming rule shared by the producers and the texture-card "UV guide"
    /// action.</summary>
    public static string UvGuidePathFor(string texturePngPath) =>
        Path.ChangeExtension(texturePngPath, null) + ".uvguide.png";

    /// <summary>
    /// Per-texture UV guides: for every workspace texture PNG this part's renderer materials reference, plot
    /// the part's own sampling submeshes onto the texture's <c>.uvguide.png</c> sibling
    /// (<see cref="UvGuidePathFor"/>), sized from the texture itself. A submesh's islands land identically on
    /// every map of its material (one UV0). Merge-plot (<see cref="UvGuide.TryRenderMerge"/>): a texture
    /// sampled by several parts accumulates each part's islands as those parts materialize. Failures are
    /// logged per texture, never fatal to the part export.
    /// </summary>
    internal static void WriteTextureUvGuides(UnityMesh mesh, PartTextures partTex,
        IReadOnlyDictionary<string, string> pngByName, IProgress<string>? log, string part,
        Action<string>? onSelfWrite = null)
    {
        var byPng = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var subs = partTex.Submeshes;
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var maps = s < subs.Count ? subs[s].AllMaps : null;
            if (maps is null) continue;
            foreach (var name in maps)
                if (pngByName.TryGetValue(name, out var png))
                    (byPng.TryGetValue(png, out var l) ? l : byPng[png] = new()).Add(s);
        }
        foreach (var (png, subIdx) in byPng)
        {
            try
            {
                int gw = UvGuide.DefaultSize, gh = UvGuide.DefaultSize;
                if (PngInfo.TrySize(png) is { } size) { gw = size.Width; gh = size.Height; }
                var guidePath = UvGuidePathFor(png);
                onSelfWrite?.Invoke(guidePath);
                if (UvGuide.TryRenderMerge(mesh, subIdx, gw, gh, guidePath))
                    log?.Report($"uv    {part}: guide → textures/{Path.GetFileName(guidePath)} ({gw}×{gh}, {subIdx.Count} submesh)");
            }
            catch (Exception e) { log?.Report($"uv    {part}: guide for {Path.GetFileName(png)} failed — {e.Message}"); }
        }
    }

    /// <summary>
    /// Build one texture's UV guide ON DEMAND — the map card's UV button must work before anything is
    /// materialized. The caller hands the samplers it read off the subject tree: each (lod0 mesh
    /// <c>m_Name</c>, its recipe mesh ADDRESS, submesh index, and the part's edited workspace glb when it has
    /// one) whose renderer material references the texture. Each mesh loads PREFERRING the modder's edited
    /// glb — the guide must show the UVs the mod ships, not the vanilla layout — falling back to the
    /// catalog-resolved game copy. Sampling submeshes merge-plot onto <paramref name="guidePath"/>, sized
    /// from the texture's own metadata. Returns null on success (≥1 mesh plotted), else the user-facing
    /// reason.
    /// </summary>
    public static string? BuildUvGuideOnDemand(GameVfs vfs,
        string textureName, string textureBundleId,
        IReadOnlyList<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> samplers, string guidePath)
    {
        if (samplers.Count == 0)
            return $"No part of this subject samples {textureName}. Nothing to draw.";
        var reader = new BundleReader();
        var decCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[]? Dec(string logical)
        {
            if (decCache.TryGetValue(logical, out var cached)) return cached;
            byte[]? bytes;
            try { bytes = vfs.TryDeobfuscateLogical(logical); } catch { bytes = null; }
            return decCache[logical] = bytes;
        }

        int gw = UvGuide.DefaultSize, gh = UvGuide.DefaultSize;
        var texDec = Dec(textureBundleId);
        if (texDec is not null && reader.GetTextureMeta(texDec, textureName) is { } meta && meta.Width > 0 && meta.Height > 0)
        { gw = meta.Width; gh = meta.Height; }

        // the vanilla fallback: catalog-resolve the address to its bundle and decode the game mesh
        UnityMesh? ResolveVanilla(string meshName, string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            var bundle = vfs.Catalog.ResolveAddress(address);
            if (bundle is null) return null;
            var dec = Dec(bundle);
            if (dec is null) return null;
            var field = reader.GetMeshField(dec, meshName);
            return field is null ? null : UnityMesh.Decode(field);
        }

        return PlotUvGuide(samplers, gw, gh, textureName, guidePath, ResolveVanilla);
    }

    /// <summary>The guide-rendering core, split out so the modded-vs-vanilla mesh choice is testable without
    /// a <see cref="GameVfs"/>. For each distinct mesh among <paramref name="samplers"/> it loads the
    /// geometry — PREFERRING the part's edited workspace glb (<c>ModdedGlb</c>), falling back to
    /// <paramref name="resolveVanilla"/> when the part is unedited or the glb won't read — then merge-plots
    /// its sampling submeshes onto <paramref name="guidePath"/> at
    /// <paramref name="gw"/>×<paramref name="gh"/>. The guide is rebuilt FRESH (prior file deleted) so an
    /// edit's new UV layout replaces the stale one instead of overlaying it. A submesh index past a
    /// merged-down edited mesh's submesh count is skipped, never a crash. Returns null on success (≥1 mesh
    /// plotted), else the user-facing reason.</summary>
    internal static string? PlotUvGuide(
        IReadOnlyList<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> samplers,
        int gw, int gh, string textureName, string guidePath,
        Func<string, string, UnityMesh?> resolveVanilla)
    {
        try { if (File.Exists(guidePath)) File.Delete(guidePath); } catch { /* rebuild overwrites/merges */ }

        int plotted = 0;
        foreach (var group in samplers.GroupBy(s => s.MeshName, StringComparer.Ordinal))
        {
            UnityMesh? mesh = null;
            var moddedGlb = group.Select(s => s.ModdedGlb).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            if (moddedGlb is not null)
            {
                try { mesh = MeshGltf.ImportGlb(moddedGlb, null); }   // single-mesh workspace glb → take the sole mesh
                catch { mesh = null; }                                // unreadable edit → fall back to the game mesh
            }
            mesh ??= resolveVanilla(group.Key, group.Select(s => s.MeshAddress).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? "");
            if (mesh is null) continue;
            if (UvGuide.TryRenderMerge(mesh, group.Select(s => s.Submesh).ToList(), gw, gh, guidePath))
                plotted++;
        }
        return plotted > 0 ? null
            : $"Couldn't read the meshes that sample {textureName} from the game. Rescan, then try again.";
    }

    /// <summary>Resolve a part's renderer-bound textures to their on-disk workspace PNGs. Every producer
    /// writes <c>textures/&lt;name&gt;.&lt;bundle&gt;.png</c> via
    /// <see cref="TextureExport.BundleScopedName"/> and records that bundle on the project target, so
    /// <paramref name="recordedBundles"/> is the primary source: it names the exact file the producer wrote
    /// and survives a game rescan. A genuinely unmaterialized texture resolves by the renderer PPtr's pinned
    /// bundle; there is no name-convention re-derivation, and resolving name-only would miss every file and
    /// strip the rebuilt glb of its maps. A resolved path that isn't on disk is reported once per texture.
    /// Returns the part-level base/normal PNGs and the per-submesh (base, normal, RMO) set — the maps the
    /// Blender-facing glb embeds.</summary>
    internal static (string? BaseColor, string? Normal, List<(string?, string?, string?)> PerSubmesh) ResolvePartPngs(
        string texDir, string subjectSlug, PartTextures partTex, string part, IProgress<string>? log,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? recordedBundles = null)
    {
        // one resolution per texture name (base/normal/submesh all share the same file), warned once if missing
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var t in partTex.All)
        {
            // Prefer the PROJECT'S RECORDED bundle: the producer wrote textures/<name>.<bundle>.png and
            // recorded THAT bundle on the target, so this survives a game update. A renderer PPtr that pinned
            // a bundle wins (disambiguating same-named textures from different bundles); else pick the
            // recorded bundle whose scoped PNG is actually present.
            string? texHash = null;
            if (recordedBundles is not null && recordedBundles.TryGetValue(t.Name, out var recorded) && recorded.Count > 0)
                texHash = (t.Bundle is not null ? recorded.FirstOrDefault(h => string.Equals(h, t.Bundle, StringComparison.OrdinalIgnoreCase)) : null)
                          ?? recorded.FirstOrDefault(h => File.Exists(Path.Combine(texDir, TextureExport.BundleScopedName(h, t.Name, subjectSlug))))
                          ?? recorded[0];
            // no recorded target ⇒ unmaterialized: the renderer PPtr's pinned bundle is the only identity
            texHash ??= t.Bundle;
            // a null bundle folds to the deterministic "_" segment (as the producers do); that file was never
            // written, so File.Exists is false and the miss is reported below
            var file = Path.Combine(texDir, TextureExport.BundleScopedName(texHash ?? "", t.Name, subjectSlug));
            if (File.Exists(file)) resolved[t.Name] = file;
            else
            {
                resolved[t.Name] = null;
                log?.Report($"      ({part}: texture {t.Name} not found at textures/{Path.GetFileName(file)} — its submesh rebuilds untextured)");
            }
        }
        string? Png(string? name) => name is not null && resolved.TryGetValue(name, out var f) ? f : null;
        var perSubmesh = partTex.Submeshes
            .Select(sm => (Png(sm.BaseColor), Png(sm.Normal), Png(sm.Rmo)))
            .ToList<(string?, string?, string?)>();
        return (Png(partTex.All.FirstOrDefault(t => t.IsBaseColor).Name),
                Png(partTex.All.FirstOrDefault(t => t.IsNormal).Name), perSubmesh);
    }

    /// <summary>The project's recorded Texture2D bundles, object-name → the distinct source bundles it was
    /// materialized from (the game carries same-named textures in different bundles as distinct assets).
    /// <see cref="ResolvePartPngs"/> keys on this, so a rig rebuild after a game rescan reads the SAME
    /// workspace PNG the producer wrote rather than re-deriving the bundle from a newer index.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> RecordedTextureBundles(ModProject project)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in project.Targets)
            if (t.AssetType == "Texture2D" && !string.IsNullOrEmpty(t.Bundle))
            {
                if (!map.TryGetValue(t.ObjectName, out var list)) map[t.ObjectName] = list = new();
                if (!list.Contains(t.Bundle, StringComparer.OrdinalIgnoreCase)) list.Add(t.Bundle);
            }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    private static string Safe(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_'));
}
