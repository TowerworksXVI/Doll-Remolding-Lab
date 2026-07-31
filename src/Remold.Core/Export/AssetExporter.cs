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
        // v5: the version moves whenever the combined build's own output rules change — a cached glb built
        // to an older rule renders parts at poses this build no longer writes, so it must rebuild rather
        // than open.
        sb.Append("combined-v5\n").Append(catalogVersion).Append('\n');
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
        var reader = new BundleReader();
        var report = new ExportReport { OutputDir = outDir };
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
    /// <param name="ct">Observed between parts, so a speculative build gives the machinery back promptly
    /// when somebody asks for it. Cancelling throws before <paramref name="combinedOut"/> is written, which
    /// is what keeps a half-built session off disk.</param>
    public static IReadOnlyList<string> BuildRiggedGlbs(string anyGamePath, GameVfs vfs,
        Outfit outfit, string character, IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut, IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
        string texDir, IProgress<string>? log = null, string? combinedOut = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? recordedTextureBundles = null,
        ICollection<string>? vanillaFallbacks = null, CancellationToken ct = default)
    {
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
        foreach (var p in parts) CollectBones(p.SourceBundle, Dec(p.SourceBundle));
        var bones = BoneTable.FromMap(vfs.CatalogVersion, boneMap);

        var done = new List<string>();
        var rigged = new List<MeshGltf.RiggedPart>();
        bool anyHashNamedRig = false;   // a skinned part with NO scene rig falls back to the bone table
        // Every part's game skin against what its scene rig names, in the order they are read — the subject's
        // whole answer for "which path does this bone hash take", which only the finished loop holds.
        var partRigs = new List<(MeshSkin Skin, IReadOnlyList<string>? BonePaths)>();
        // Edited parts, by their slot in `rigged`: their paths need that whole answer, so they are named
        // after the loop.
        var editedParts = new List<(int Slot, MeshSkin Skin)>();
        foreach (var (part, srcBundle, meshName, glbOut, bakedRest, pathId, editedGlb) in parts)
        {
            // OUTSIDE the per-part catch, which would swallow it and carry on building.
            ct.ThrowIfCancellationRequested();
            // per-part isolation: one part failing to decode/rig must not abort the rest
            try
            {
                var dec = Dec(srcBundle);
                if (dec is null) continue;
                var field = reader.GetMeshField(dec, meshName, pathId);
                if (field is null) continue;
                // Named by the RECORDED name, which is the renderer slot's; on some enemy/prop slots the
                // mesh asset's own m_Name differs, and the send-back joins its parts to the ledger by the
                // name the glb carries.
                var mesh = UnityMesh.Decode(field, meshName);
                var skin = MeshSkin.Decode(field);
                var partTex = PartTextureResolver.Resolve(scope, reader, Dec, outfit, part, mesh.Submeshes.Count);
                // the renderer bound no textures — the preview rebuilds untextured, but never silently
                if (partTex.All.Count == 0)
                    log?.Report($"      ({part}: no textures resolved from its prefab renderer — the preview is untextured)");
                // resolve each renderer texture to its bundle-scoped workspace PNG; a resolved-but-absent map
                // is reported per texture, never silently dropped
                var (baseColorPng, normalPng, perSubmesh) = ResolvePartPngs(texDir, subjectSlug, partTex, part, log, recordedTextureBundles);
                // A recorded rest that is not an axis-aligned rotation cannot be un-baked by transpose, so
                // the export skips it and the part lands in bind space rather than a skewed one.
                var recordedRest = RestBake.FromList(bakedRest, out bool restRefused);
                if (restRefused)
                    log?.Report($"      ({part}: its recorded rest pose is not an axis-aligned rotation; the export skips it)");
                if (skin is { IsSkinned: true })
                {
                    // scene rig for NAMES/parenting; the bake replays the target's record, so this rebuild
                    // lands in the same space the Add put the workspace in. The mesh-bundle read comes first
                    // (classes whose own bundle carries the scene), selected by the path id where there is
                    // one — the recorded name is the slot's and need not name the mesh object. smr-body
                    // parts fall back to the assembly prefab, keyed by the slot's mesh reference. Read ONCE
                    // per part: an edited part needs it too, for the map and for its connectors.
                    var sceneRig = SceneRig.TryRead(dec, meshName, skin, pathId)
                        ?? (pathId != 0 && scope.Candidates.Count > 0
                            ? SceneRig.TryReadForMeshRef(scope.Candidates[0].Dec, pathId, skin)
                            : null);
                    // the rig's paths are in GAME bone order, so they pair with this part's game skin
                    partRigs.Add((skin, sceneRig?.BonePaths));
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
                        if (edited is { } e)
                        {
                            editedParts.Add((rigged.Count, e.Skin));
                            rigged.Add(new MeshGltf.RiggedPart(e.Mesh, e.Skin, baseColorPng, normalPng,
                                ConnectorRests: Composed(sceneRig?.ConnectorRests, uprighting),
                                PerSubmesh: perSubmesh));
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
                        MeshGltf.ExportRiggedGlb(mesh, skin, bones.Path, glbOut, baseColorPng, normalPng, perSubmesh,
                            sceneRig?.BonePaths, uprighting, sceneRig?.ConnectorRests);
                    var (contextPose, connectors) = CombinedPose(sceneRig, uprighting,
                        // the gated read is the expensive half, so it runs only where the answer can matter
                        () => Workbench.PartSkinGate.Blocked(Dec, srcBundle, meshName, pathId, reader) is null);
                    rigged.Add(new MeshGltf.RiggedPart(mesh, skin, baseColorPng, normalPng,
                        sceneRig?.BonePaths, uprighting, connectors,
                        PerSubmesh: perSubmesh,
                        ContextPose: contextPose));   // for the union combined
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
        ct.ThrowIfCancellationRequested();
        if (combinedOut is not null && rigged.Count >= 2)
            MeshGltf.ExportCombinedRiggedGlb(rigged, bones.Path, combinedOut);
        return done;
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
