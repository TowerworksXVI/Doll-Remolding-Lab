using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Textures;

namespace Remold.Core.Workbench;

/// <summary>The result of a first-touch materialization: whether the asset was freshly created, already
/// present (idempotent no-op), or the export failed loudly.</summary>
public enum MaterializeOutcome { Created, AlreadyPresent, Failed, Skipped }

/// <summary>The outcome plus the project targets that now cover the touched asset. Empty
/// <see cref="Targets"/> only on <see cref="MaterializeOutcome.Failed"/>. <see cref="Warning"/> is a
/// non-fatal note carried on a <see cref="MaterializeOutcome.Created"/> part.</summary>
public sealed record MaterializeResult(MaterializeOutcome Outcome, string? Error, IReadOnlyList<ProjectTarget> Targets, string? Warning = null)
{
    public bool Usable => Outcome != MaterializeOutcome.Failed;
    internal static MaterializeResult Fail(string error) => new(MaterializeOutcome.Failed, error, Array.Empty<ProjectTarget>());
    /// <summary>A silent no-op: the texture's live format can't be authored as a PNG (HDR/float), so it is
    /// not materialized and not a failure — callers drop it without noise.</summary>
    internal static MaterializeResult Skip() => new(MaterializeOutcome.Skipped, null, Array.Empty<ProjectTarget>());
}

/// <summary>The result of the OFF-THREAD prepare phase for a part materialize: a carried
/// <see cref="PartMaterializePrep"/> to commit, or a soft <see cref="Error"/>. A hard BUSY
/// (<see cref="IOException"/>, game running) is thrown, not returned.</summary>
internal sealed record PartPrepResult(PartMaterializePrep? Prep, string? Error)
{
    public bool Ok => Prep is not null;
}

/// <summary>Everything <see cref="Materializer.CommitPart"/> needs to add the exported part to the project on
/// the writer (UI) thread, captured off-thread so no project state is touched there (single-writer).</summary>
internal sealed record PartMaterializePrep(ExportReport Report, string Character, string Stem, string MeshPrefix, string ModRoot, string PartToken);

/// <summary>The result of the OFF-THREAD prepare phase for a texture materialize (decode + PNG-encode + the
/// <c>originals/</c> baseline). A hard BUSY (<see cref="IOException"/>) is thrown, not returned.</summary>
internal sealed record TexturePrepResult(TextureMaterializePrep? Prep, string? Error, bool Skip = false)
{
    public bool Ok => Prep is not null;
}

/// <summary>Everything <see cref="Materializer.CommitTexture"/> needs to add the exported texture to the
/// project on the writer (UI) thread.</summary>
internal sealed record TextureMaterializePrep(ExportReport Report, string ModRoot, string TextureName,
    string BundleId, string Character, string Stem);

/// <summary>
/// The first-touch materialization seam for the Outfit Workbench: turns an untargeted subject node into
/// editable copies on demand, reusing the EXACT Add machinery so a materialize and an Add produce
/// byte-identical project shape.
///
/// <para><b>Two grains.</b> A <b>mesh-shaped</b> touch materializes the whole PART via
/// <see cref="AssetExporter.ExportRecipePart"/> + <see cref="ProjectBuilder.AddExport"/>, because the mesh
/// baseline needs the part machinery (rig capture, LOD slots, baked rest). A <b>map</b> touch materializes
/// just THAT texture via <see cref="TextureExport.ExportPng"/> through
/// <see cref="ProjectBuilder.AddExport"/> — one PNG encode. Both grains share the SAME target-add path,
/// so idempotency spans them in either direction.</para>
///
/// <para><b>Off-thread read, on-thread commit (single-writer).</b> The project's target list tolerates no
/// concurrent writer, so the live game read/encode/copy runs OFF the UI thread and touches no project state,
/// while the target add runs on the writer thread and re-checks idempotency — two queued materializes can
/// both pass the caller's outer guard. The <c>*Async</c> pair composes that for the App; the synchronous
/// <see cref="MaterializePart"/>/<see cref="MaterializeTexture"/> fuse both steps inline for the tests.</para>
///
/// <para>This class also owns the single definition of "which parts are materialized", so the workbench,
/// Pick's exported ✓ restore, and the idempotency guard can never drift apart.</para>
/// </summary>
public static class Materializer
{
    /// <summary>The resolver <see cref="ProjectTarget.Source"/> tag a workbench-materialized texture carries:
    /// <c>"material"</c>, the renderer-exact tier.</summary>
    public const string MaterialSource = "material";

    /// <summary>The subject's export subfolder (<c>{slug(character)}_{stem}</c>) — the stable key a mesh
    /// target's <see cref="ProjectTarget.ReplaceFile"/> starts with. Mirrors the Add worker's layout.</summary>
    public static string SubjectFolder(string character, string stem) => ModNaming.SubjectSlug(character, stem);

    /// <summary>The part tokens already materialized for one outfit: a mesh target under the outfit's
    /// <see cref="SubjectFolder"/>, keyed by the part its object name names. THE single definition, so
    /// exported-ness never drifts from materialized-ness.</summary>
    public static HashSet<string> MaterializedParts(ModProject project, string character, string stem, string meshPrefix)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in PartTokenMap(project, character, stem, meshPrefix).Keys) set.Add(token);
        return set;
    }

    /// <summary>Part token → the subject's mesh targets carrying it, derived through
    /// <see cref="SubjectModelBuilder.SlotTokens"/> — the SAME rule the workbench tree and the Pick roster
    /// name their parts with. The subject's own mesh prefix is not enough on its own: an outfit that reuses
    /// another family's mesh (a dorm skin on the base face or hair) carries that family's prefix on the
    /// target, which this subject's prefix cannot strip, and a prefix-only derivation would answer with the
    /// whole raw name and never match the part token the recipe carries.</summary>
    private static Dictionary<string, List<ProjectTarget>> PartTokenMap(ModProject project, string character,
        string stem, string meshPrefix)
    {
        var sub = SubjectFolder(character, stem);
        var mine = new List<ProjectTarget>();
        foreach (var t in project.Targets)
        {
            if (t.AssetType != "Mesh") continue;
            var rel = t.ReplaceFile.Replace('\\', '/');
            int slash = rel.IndexOf('/');
            if (slash < 0 || !rel.AsSpan(0, slash).Equals(sub, StringComparison.OrdinalIgnoreCase)) continue;
            mine.Add(t);
        }
        // The rule is a WHOLE-SUBJECT question (a short token is declined when something already derives
        // it), so it is built once over every one of the subject's mesh targets.
        var names = mine.Select(t => t.ObjectName).ToList();
        var tokenOf = SubjectModelBuilder.SlotTokens(names, meshPrefix);
        // Two foreign families competing for one short token are settled by whoever the rule reaches first,
        // and the tree reaches them in PREFAB SLOT order, which a target list cannot reproduce. Reading the
        // names backwards as well exposes exactly those names: an answer that moves is an answer this side
        // is not entitled to, and a part left unresolved re-prepares and says so, where a confident wrong
        // answer would hand the modder another mesh under the name they clicked.
        var reversed = SubjectModelBuilder.SlotTokens(Enumerable.Reverse(names).ToList(), meshPrefix);
        var map = new Dictionary<string, List<ProjectTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in mine)
        {
            var token = tokenOf(t.ObjectName);
            if (token.Length == 0) continue;
            if (!token.Equals(reversed(t.ObjectName), StringComparison.OrdinalIgnoreCase)) continue;
            if (!map.TryGetValue(token, out var list)) map[token] = list = new List<ProjectTarget>();
            list.Add(t);
        }
        return map;
    }

    /// <summary>True when a Mesh target for this part already exists (the part-grain idempotency guard).</summary>
    public static bool IsPartMaterialized(ModProject project, string character, string stem, string meshPrefix, string partToken) =>
        MaterializedParts(project, character, stem, meshPrefix).Contains(partToken);

    /// <summary>The representative Mesh target for one materialized part, or null.</summary>
    public static ProjectTarget? PartMeshTarget(ModProject project, string character, string stem,
        string meshPrefix, string partToken) =>
        PartTargets(project, character, stem, meshPrefix, partToken).FirstOrDefault(t => t.AssetType == "Mesh");

    /// <summary>The existing Texture2D target for this asset UNDER THIS SUBJECT, keyed by
    /// (subject, bundle, object name) — the game carries same-named textures in DIFFERENT bundles as
    /// DISTINCT assets, and one texture materialized under two subjects is two targets (each outfit's
    /// edit is its own). Bundle compares case-insensitively (hex), object name ordinally.</summary>
    public static ProjectTarget? TextureTarget(ModProject project, string character, string stem,
        string bundleId, string textureName) =>
        project.Targets.FirstOrDefault(t =>
            t.AssetType == "Texture2D"
            && string.Equals(t.SubjectCharacter, character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.SubjectOutfit, stem, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Bundle, bundleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.ObjectName, textureName, StringComparison.Ordinal));

    /// <summary>True when this texture is already materialized for this subject.</summary>
    public static bool IsTextureMaterialized(ModProject project, string character, string stem,
        string bundleId, string textureName) =>
        TextureTarget(project, character, stem, bundleId, textureName) is not null;

    /// <summary>Merge <paramref name="ownerMeshNames"/> into the subject's EXISTING texture target's
    /// <see cref="ProjectTarget.Users"/> (the Edit-nesting badge source). Ordinal-deduped, matching
    /// <see cref="ProjectBuilder"/>'s users-union sibling including its null-Users rule: a non-empty merge
    /// CREATES the list, an empty one leaves null null. Returns true if the set changed (the caller
    /// persists). Writer-thread only.</summary>
    public static bool MergeTextureUsers(ModProject project, string character, string stem,
        string bundleId, string textureName, IReadOnlyList<string> ownerMeshNames)
    {
        var t = TextureTarget(project, character, stem, bundleId, textureName);
        if (t is null || ownerMeshNames.Count == 0) return false;
        bool changed = false;
        foreach (var u in ownerMeshNames)
            if (!(t.Users ??= new()).Contains(u, StringComparer.Ordinal)) { t.Users.Add(u); changed = true; }
        return changed;
    }

    // ---- mesh-grain (the exact Add path) --------------------------------------------------------------

    /// <summary>
    /// Materialize ONE part at the mesh grain, idempotently, through the RECIPE-EXACT route — the fused
    /// <see cref="PreparePart"/> + <see cref="CommitPart"/> for SYNCHRONOUS callers (the tests). An
    /// already-materialized part returns <see cref="MaterializeOutcome.AlreadyPresent"/> and touches nothing.
    /// The App shell must NOT call this from a background thread; it runs the two phases separately so the
    /// commit lands on the writer thread. Geometry-only — the rig and the textured glb are rebuilt lazily by
    /// the Open-in-Blender caller. The caller owns autosave and the folder mint for a New Mod.
    /// </summary>
    public static MaterializeResult MaterializePart(ModProject project, GameVfs vfs, string gameDir,
        Outfit outfit, string character, string modRoot, RecipePart recipe,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (IsPartMaterialized(project, character, outfit.Stem, outfit.MeshPrefix, recipe.Token))
            return new MaterializeResult(MaterializeOutcome.AlreadyPresent, null, PartTargets(project, character, outfit.Stem, outfit.MeshPrefix, recipe.Token));

        var prep = PreparePart(vfs, gameDir, outfit, character, modRoot, recipe, log, ct);
        return prep.Ok ? CommitPart(project, prep.Prep!) : MaterializeResult.Fail(prep.Error!);
    }

    /// <summary>Materialize ONE part for the App: run the live game read/export OFF the UI thread, then
    /// marshal the target add back to the writer thread through <paramref name="onWriter"/>, which also runs
    /// the still-selected re-check. Returns null when the subject was removed between the read and the commit
    /// — the caller discards the work and reports the skip. A sharing violation while the game runs surfaces
    /// as an <see cref="IOException"/> (BUSY) out of the await.</summary>
    /// <param name="onSelfWrite">Forwarded to <see cref="AssetExporter.ExportRecipePart"/>: every
    /// <c>textures/</c> path this run is about to write, announced before the write. Called on the export's
    /// thread, not the writer thread.</param>
    public static async Task<MaterializeResult?> MaterializePartAsync(ModProject project, GameVfs vfs,
        string gameDir, Outfit outfit, string character, string modRoot, RecipePart recipe,
        Func<Func<MaterializeResult?>, MaterializeResult?> onWriter,
        IProgress<string>? log = null, CancellationToken ct = default, Action<string>? onSelfWrite = null)
    {
        if (IsPartMaterialized(project, character, outfit.Stem, outfit.MeshPrefix, recipe.Token))
            return new MaterializeResult(MaterializeOutcome.AlreadyPresent, null,
                PartTargets(project, character, outfit.Stem, outfit.MeshPrefix, recipe.Token));
        var prep = await Task.Run(() => PreparePart(vfs, gameDir, outfit, character, modRoot, recipe, log, ct, onSelfWrite), ct);
        if (!prep.Ok) return MaterializeResult.Fail(prep.Error!);
        return onWriter(() => CommitPartIfSelected(project, prep.Prep!));
    }

    /// <summary>PREPARE (off-thread): the RECIPE-EXACT live read/export for one part — the mesh is read by the
    /// recipe's slot name in the catalog-pinned bundle, never re-derived from prefix+token, and a broken
    /// recipe → address → bundle → mesh link FAILS LOUDLY (surfaced by <see cref="CommitPart"/>). Builds the
    /// subject's <see cref="SubjectScope"/> here, one per prepare, over the run's own deobfuscate memo. No
    /// project state touched. A sharing violation is thrown as <see cref="IOException"/> for the shell's BUSY
    /// catch.</summary>
    internal static PartPrepResult PreparePart(GameVfs vfs, string gameDir, Outfit outfit, string character,
        string modRoot, RecipePart recipe,
        IProgress<string>? log = null, CancellationToken ct = default, Action<string>? onSelfWrite = null)
    {
        var outDir = Path.Combine(modRoot, SubjectFolder(character, outfit.Stem));
        ExportReport report;
        try
        {
            // memoized deobfuscation for the scope's candidate/CAB reads; IOException propagates (BUSY, below)
            var decCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            byte[]? Deobfuscate(string logical) =>
                decCache.TryGetValue(logical, out var hit) ? hit : decCache[logical] = vfs.TryDeobfuscateLogical(logical);
            var scope = SubjectScope.Build(vfs.Catalog, Deobfuscate, outfit);
            report = AssetExporter.ExportRecipePart(gameDir, vfs, scope, outfit, character, recipe, outDir, log,
                ct, sharedRoot: modRoot, onSelfWrite: onSelfWrite);
        }
        catch (IOException) { throw; }   // sharing violation (game running) = BUSY, the shell retries
        catch (Exception e) { return new PartPrepResult(null, e.Message); }

        if (ct.IsCancellationRequested && report.CompletedParts.Count == 0)
            return new PartPrepResult(null, "cancelled before the part finished");

        return new PartPrepResult(new PartMaterializePrep(report, character, outfit.Stem, outfit.MeshPrefix, modRoot, recipe.Token), null);
    }

    /// <summary>COMMIT (writer thread): add the prepared part's targets to the project. Re-checks idempotency —
    /// two queued materializes can both pass the caller's outer guard, so the second is a no-op here. Mutates
    /// <see cref="ModProject.Targets"/>; MUST run on the single-writer (UI) thread.</summary>
    internal static MaterializeResult CommitPart(ModProject project, PartMaterializePrep prep)
    {
        if (IsPartMaterialized(project, prep.Character, prep.Stem, prep.MeshPrefix, prep.PartToken))
            return new MaterializeResult(MaterializeOutcome.AlreadyPresent, null,
                PartTargets(project, prep.Character, prep.Stem, prep.MeshPrefix, prep.PartToken));

        ProjectBuilder.AddExport(project, prep.Report, prep.ModRoot, prep.Character, prep.Stem);
        // "Created" only when a mesh target actually landed: the export marks a part completed even when its
        // mesh couldn't be read, so guarding on CompletedParts alone would report success with zero targets.
        var targets = PartTargets(project, prep.Character, prep.Stem, prep.MeshPrefix, prep.PartToken);
        if (!targets.Any(t => t.AssetType == "Mesh"))
        {
            // surface the link-specific reason (recipe → address → bundle → mesh), not a generic string
            var meshFail = prep.Report.Files
                .Where(f => f.Kind == "mesh" && !f.Ok).Select(f => (ExportedFile?)f).FirstOrDefault();
            var readFail = $"couldn't read '{prep.PartToken}' from the game files";
            if (meshFail is not null) return MaterializeResult.Fail(meshFail.Value.Note ?? readFail);
            // No FAILED mesh either: the export ran clean but nothing it staged carries this part's token
            // (PartTargets re-derives it from each target's ObjectName). That's a naming mismatch, not a
            // read failure — don't blame the game files for it.
            return MaterializeResult.Fail(prep.Report.Files.Any(f => f.Kind == "mesh" && f.Ok)
                ? $"no mesh for '{prep.PartToken}' in the export"
                : readFail);
        }
        // Carry the loud texture miss forward: the export stages a non-Ok "texture" finding when the prefab
        // renderer bound no maps, so the part committed but exports untextured. Surface it as a Warning.
        var texWarn = prep.Report.Files
            .Where(f => f.Kind == "texture" && !f.Ok).Select(f => (ExportedFile?)f).FirstOrDefault();
        return new MaterializeResult(MaterializeOutcome.Created, null, targets, texWarn?.Note);
    }

    /// <summary>COMMIT the prepared part ONLY if its subject is still selected: a materialize that raced a
    /// subject removal between Prepare and Commit must not resurrect the removed subject's targets. Returns
    /// null when the subject's <see cref="SelectionEntry"/> is gone. Writer-thread only.</summary>
    internal static MaterializeResult? CommitPartIfSelected(ModProject project, PartMaterializePrep prep) =>
        project.HasSubject(prep.Character, prep.Stem) ? CommitPart(project, prep) : null;

    /// <summary>COMMIT the prepared texture ONLY if the subject that triggered it is still selected. A
    /// texture prep carries no subject (a map is shared), so the caller passes the working subject explicitly.
    /// Returns null when that subject was removed mid-prepare. Writer-thread only.</summary>
    internal static MaterializeResult? CommitTextureIfSelected(ModProject project, TextureMaterializePrep prep,
        string character, string stem) =>
        project.HasSubject(character, stem) ? CommitTexture(project, prep) : null;

    /// <summary>Run <paramref name="commit"/> ONLY when the project the off-thread prepare captured is still
    /// the one the app has open. New Mod / Open swaps the app's project object while a prepare is in flight;
    /// committing the abandoned project would mutate ITS targets and write into ITS folder while the app
    /// autosaves the new one. On a switch this returns null, like the subject-removed guards. REFERENCE
    /// identity, not value equality: a distinct project selecting the same subject is still a switch.
    /// Writer-thread only.</summary>
    public static MaterializeResult? CommitIfCurrentProject(ModProject captured, ModProject current,
        Func<MaterializeResult?> commit) =>
        ReferenceEquals(captured, current) ? commit() : null;

    /// <summary>The subject's mesh targets for one part token, in project order.</summary>
    public static IReadOnlyList<ProjectTarget> PartTargets(ModProject project, string character, string stem,
        string meshPrefix, string partToken) =>
        PartTokenMap(project, character, stem, meshPrefix).TryGetValue(partToken, out var hit)
            ? hit
            : Array.Empty<ProjectTarget>();

    // ---- map-grain (the sub-second single-texture path) -----------------------------------------------

    /// <summary>The workspace PNG path a materialized texture lands at: under <c>textures/</c> at the mod
    /// root, with the source bundle AND the subject folded into the filename
    /// (<c>&lt;name&gt;.&lt;bundle&gt;.&lt;subject&gt;.png</c>) — same-named textures from different
    /// bundles never collide, and each subject edits its OWN file, which is what scopes the edit to the
    /// outfit it was made on. The immediate parent stays <c>textures/</c>, so the edit watcher's dir
    /// filter is unaffected. The texture name is carried on the target — never re-parsed from this
    /// filename.</summary>
    public static string TextureWorkspacePath(string modRoot, string character, string stem,
        string bundleId, string textureName) =>
        Path.Combine(modRoot, "textures",
            TextureExport.BundleScopedName(bundleId, textureName, SubjectFolder(character, stem)));

    /// <summary>The pristine baseline path for a materialized texture, scoped like
    /// <see cref="TextureWorkspacePath"/> — the parent stays <c>originals/</c> so the watcher excludes
    /// it.</summary>
    public static string TextureOriginalPath(string modRoot, string character, string stem,
        string bundleId, string textureName) =>
        Path.Combine(modRoot, "originals",
            TextureExport.BundleScopedName(bundleId, textureName, SubjectFolder(character, stem)));

    /// <summary>Materialize ONE texture at the map grain, idempotently — the fused
    /// <see cref="PrepareTexture"/> + <see cref="CommitTexture"/> for SYNCHRONOUS callers (the tests).
    /// Decodes + PNG-encodes just this texture, persists the <c>originals/</c> baseline, and feeds the result
    /// through <see cref="ProjectBuilder.AddExport"/> so the target's shape, dedupe, and layout are
    /// byte-identical to Add's.</summary>
    /// <param name="users">The slot names of the subject-model parts whose materials bind this texture — the
    /// same names the mesh-grain route records. Deduped onto the target's
    /// <see cref="ProjectTarget.Users"/>.</param>
    public static MaterializeResult MaterializeTexture(ModProject project, GameVfs vfs,
        string modRoot, string textureName, string bundleId, IReadOnlyList<string> users,
        string character, string stem)
    {
        var existing = TextureTarget(project, character, stem, bundleId, textureName);
        if (existing is not null)
            return new MaterializeResult(MaterializeOutcome.AlreadyPresent, null, new[] { existing });

        var prep = PrepareTexture(vfs, modRoot, textureName, bundleId, users, character, stem);
        if (prep.Skip) return MaterializeResult.Skip();
        return prep.Ok ? CommitTexture(project, prep.Prep!) : MaterializeResult.Fail(prep.Error!);
    }

    /// <summary>Materialize ONE texture for the App: decode + PNG-encode OFF the UI thread, then marshal the
    /// target add back to the writer thread through <paramref name="onWriter"/>, which runs the
    /// still-selected re-check. An already-present texture touches nothing (the caller merges owner meshes
    /// into its Users). Returns null when the subject was removed mid-prepare.</summary>
    public static async Task<MaterializeResult?> MaterializeTextureAsync(ModProject project, GameVfs vfs,
        string modRoot, string textureName, string bundleId, IReadOnlyList<string> users,
        string character, string stem, Func<Func<MaterializeResult?>, MaterializeResult?> onWriter,
        CancellationToken ct = default)
    {
        var existing = TextureTarget(project, character, stem, bundleId, textureName);
        if (existing is not null)
            return new MaterializeResult(MaterializeOutcome.AlreadyPresent, null, new[] { existing });
        var prep = await Task.Run(() => PrepareTexture(vfs, modRoot, textureName, bundleId, users, character, stem), ct);
        if (prep.Skip) return MaterializeResult.Skip();
        if (!prep.Ok) return MaterializeResult.Fail(prep.Error!);
        return onWriter(() => CommitTextureIfSelected(project, prep.Prep!, character, stem));
    }

    /// <summary>PREPARE (off-thread): decode + PNG-encode the texture and write the <c>originals/</c> baseline,
    /// no project state touched. Returns the prep to commit or a soft error; a sharing violation (game running)
    /// is thrown as <see cref="IOException"/> for the shell's BUSY catch.</summary>
    internal static TexturePrepResult PrepareTexture(GameVfs vfs, string modRoot,
        string textureName, string bundleId, IReadOnlyList<string> users, string character, string stem)
    {
        try
        {
            var dec = vfs.TryDeobfuscateLogical(bundleId);
            if (dec is null) return new TexturePrepResult(null, $"couldn't read the bundle holding '{textureName}'");

            var reader = new BundleReader();

            // SILENTLY skip a format the codec can't re-encode (HDR/float, e.g. RGBAHalf) BEFORE writing any
            // PNG: decoding it would flatten an HDR map to a lossy 8-bit image that only Build later rejects.
            // A header read catches it. Enforced here, where the texture is read, so no materialize route can
            // slip an un-authorable map into the project.
            var meta = reader.GetTextureMeta(dec, textureName);
            if (meta is { } probe && !TextureCodec.IsSupported((AssetsTools.NET.Texture.TextureFormat)probe.Format))
                return new TexturePrepResult(null, null, Skip: true);

            var outFile = TextureWorkspacePath(modRoot, character, stem, bundleId, textureName);

            if (!TextureExport.ExportPng(reader, dec, textureName, outFile))
                return new TexturePrepResult(null, $"'{textureName}' wasn't found in its bundle");

            var origFile = TextureOriginalPath(modRoot, character, stem, bundleId, textureName);
            Directory.CreateDirectory(Path.GetDirectoryName(origFile)!);
            File.Copy(outFile, origFile, overwrite: true);

            var report = new ExportReport { OutputDir = modRoot };
            report.Files.Add(new ExportedFile("texture", textureName, outFile, true, null,
                Bundle: bundleId, OriginalPath: origFile,
                Users: users.Count > 0 ? users.ToArray() : null,
                TextureMeta: meta, Source: MaterialSource));
            return new TexturePrepResult(new TextureMaterializePrep(report, modRoot, textureName, bundleId,
                character, stem), null);
        }
        catch (IOException) { throw; }   // sharing violation (game running) = BUSY, the caller retries
        catch (Exception e) { return new TexturePrepResult(null, e.Message); }
    }

    /// <summary>COMMIT (writer thread): add the prepared texture to the project. Re-checks idempotency — a
    /// peer materialize may have landed it first. MUST run on the single-writer (UI) thread.</summary>
    internal static MaterializeResult CommitTexture(ModProject project, TextureMaterializePrep prep)
    {
        var existing = TextureTarget(project, prep.Character, prep.Stem, prep.BundleId, prep.TextureName);
        if (existing is not null)
            return new MaterializeResult(MaterializeOutcome.AlreadyPresent, null, new[] { existing });

        ProjectBuilder.AddExport(project, prep.Report, prep.ModRoot, prep.Character, prep.Stem);
        var t = TextureTarget(project, prep.Character, prep.Stem, prep.BundleId, prep.TextureName);
        return t is null
            ? MaterializeResult.Fail($"'{prep.TextureName}' didn't land in the project")
            : new MaterializeResult(MaterializeOutcome.Created, null, new[] { t });
    }
}
