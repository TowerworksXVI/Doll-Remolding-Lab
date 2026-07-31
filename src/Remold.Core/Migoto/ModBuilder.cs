using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Textures;
using Remold.Core.Workbench;

namespace Remold.Core.Migoto;

/// <summary>
/// The world the build reads, as delegates so the orchestration is testable without a live install. Every
/// bundle comes through here FORWARD-resolved: an address resolves through the catalog to the bundle the
/// game actually loads, never a hand-supplied bundle id — same-named twins in other bundles are a silent
/// wrong-geometry trap. Smr-backed parts carry their resolved bundle + path id from the prefab itself.
/// </summary>
public sealed record BuildEnv(
    /// <summary>(character, outfit stem) → the subject's live roster, or null (unknown subject).</summary>
    Func<string, string, SubjectModel?> ResolveSubject,
    /// <summary>Recipe mesh address → owning logical bundle id, or null (no catalog entry).</summary>
    Func<string, string?> ResolveAddress,
    /// <summary>Logical bundle id → deobfuscated bytes, or null (not in this install).</summary>
    Func<string, byte[]?> Deobfuscate,
    /// <summary>The live asset-catalog version (sidecar stamp); null when unknown.</summary>
    string? CatalogVersion,
    /// <summary>The building app's version (sidecar stamp); null when unknown.</summary>
    string? AppVersion,
    /// <summary>The roster's asset-sharing measurement, deciding each edit's scope (draw-scoped
    /// retextures, presence latches). Null = no measurement: every edit ships unscoped, noted in the
    /// diagnostics.</summary>
    Workbench.SharingIndex? Sharing = null);

/// <summary>
/// Where a build may keep regenerable products (solved recovery operators, encoded textures). Both are
/// keyed so an entry can only be served to an identical input, and neither changes what a build emits —
/// only how long it takes. Null = no persistent caches: nothing read, nothing left behind.
/// </summary>
public sealed record BuildCaches(string OperatorDir, string TextureDir)
{
    /// <summary>The app's own cache locations under <see cref="LabPaths.CacheRoot"/>.</summary>
    public static BuildCaches Default => new(LabPaths.OperatorCacheRoot, LabPaths.EncodedTextureRoot);
}

/// <summary>
/// Builds the mod a project's workbench state describes: derives the edit list in memory
/// (<see cref="Workbench.VerbDerivation"/>), resolves every touched mesh forward, derives each Replace's
/// pool from its donor's weights, dumps vanilla streams, compiles each donor onto its pipeline's union,
/// encodes textures, hashes each retextured stock texture from its own bundle bytes, enumerates every
/// shipped tier's hashes for non-pool hides, and emits the self-contained 3DMigoto folder + <c>gf2mod.json</c>
/// sidecar (+ the distribution <c>.zip</c>). Each Replace becomes one emitter pipeline, processed in roster
/// order of its replaced part for stable rebuilds; a part pooled by several Replaces is dumped once. The
/// emitters stay bundle-agnostic — this is the single place game-install knowledge enters the build.
///
/// <para><b>Mid-failure story:</b> everything is written into a work dir and a temp mod dir; the final
/// folder and zip appear only after a fully successful build (delete-then-move swap). A failed build throws
/// with the work dirs cleaned up — never a half-mod at the final path.</para>
/// </summary>
public static class ModBuilder
{
    /// <summary>What one build produced. <paramref name="Warnings"/> are user-facing and actionable (the
    /// Build pane lists and counts them). <paramref name="Infos"/> are user-facing disclosures with nothing
    /// to fix: an edit's reach beyond the picked subject, and how the build scoped it.
    /// <paramref name="Diagnostics"/> are everything else, reaching the build log and no UI surface.</summary>
    public sealed record Result(string OutDir, string? ZipPath,
        IReadOnlyList<string> Warnings, IReadOnlyList<string> Infos, IReadOnlyList<string> Diagnostics);

    /// <summary>What the build needs to know about a stock texture it overrides or anchors on: the
    /// 3DMigoto resource hash that addresses it, and whether its DXGI format is an <c>_SRGB</c> one —
    /// the tag an authored replacement of that slot has to carry.</summary>
    private readonly record struct StockTex(string Hash, bool Srgb);

    /// <summary>The sRGB family an authored donor map of <paramref name="kind"/> has to be tagged with: the
    /// map binds at the anchor's draw, in the slot the anchor's own map of that kind occupies, so the
    /// anchor's DXGI format decides it. A map that won't read warns — a wrong tag is a colour error the
    /// author would otherwise diagnose in game — and leaves <paramref name="byConvention"/> as the only
    /// answer, which <paramref name="diagnostics"/> records.
    ///
    /// <para>EVERY matching map of every anchor material is read, because the donor map binds into all of
    /// them while the file can carry only one tag. The first readable map wins, deterministically, and a
    /// disagreement is named: the losing material renders the donor map wrong.</para></summary>
    internal static bool AnchorSrgb(IReadOnlyList<Workbench.SubjectMaterial> anchorMaterials,
        Func<string, bool> isSlot, string kind, bool byConvention, string sfx,
        Func<Workbench.SubjectMap, bool> srgbOf, List<string> warnings, List<string> diagnostics)
    {
        bool unreadable = false;
        string? wonBy = null;
        bool won = false;
        foreach (var material in anchorMaterials)
            foreach (var m in material.Maps.Where(x => isSlot(x.Slot)))
            {
                bool srgb;
                try { srgb = srgbOf(m); }
                catch (Exception ex) when (ex is not BlockedAssetException)
                {
                    unreadable = true;
                    warnings.Add($"donor ({sfx}): anchor {kind} map '{m.TextureName}' can't be read ({ex.Message})");
                    continue;
                }
                if (wonBy is null) { wonBy = m.TextureName; won = srgb; }
                else if (srgb != won)
                    warnings.Add($"donor ({sfx}): anchor {kind} maps disagree — '{wonBy}' is {Family(won)} and "
                        + $"'{m.TextureName}' is {Family(srgb)}. The authored {kind} is tagged {Family(won)}, "
                        + $"so it reads wrong wherever '{m.TextureName}' is the one being replaced");
            }
        if (wonBy is not null) return won;
        if (unreadable)
            diagnostics.Add($"donor ({sfx}): no anchor {kind} map answered, so the authored {kind} is "
                + $"tagged {Family(byConvention)} by convention");
        return byConvention;
    }

    /// <summary>The DXGI family's name for a warning.</summary>
    private static string Family(bool srgb) => srgb ? "sRGB" : "linear";

    /// <summary>One warning per hide hash claimed by two changes with different toggle keys, else null. The
    /// hash dedup leaves ONE skip section, so it can carry only one key — the first claimant's — and the
    /// second's is dropped. Same shape as the retexture collapse: what collapsed, which key survives, what
    /// to do. Two unkeyed claimants, or two on the same key, say nothing.</summary>
    internal static string? HideKeyCollisionWarning(string meshName, string? kept, string? incoming)
    {
        if (ModKeys.SameKey(kept, incoming)) return null;
        if (kept is null && incoming is null) return null;
        return $"mesh '{meshName}' is hidden by two changes with different toggle keys. One section can "
            + $"carry one key, so {kept ?? "no key"} applies. Set the same key on both";
    }

    /// <summary>The refusal when two changes ask one stock texture for different images and the mechanism
    /// has only one image to give: the game-wide rebind, or a draw-scoped section a game-wide claim also
    /// wants. Both claimants are named so the author can tell which two edits met.</summary>
    internal static InvalidOperationException ImageCollision(string textureName, string first, string second) =>
        new($"stock texture '{textureName}' is retextured with two different images: {first} and {second}. "
            + "This texture isn't scoped to one outfit, so one image would have to win. "
            + "Give both changes the same image");

    /// <summary>The refusal when ONE outfit asks one stock texture for two different images at one of its
    /// mesh draws: one draw binds one image, so the later write would win unannounced. Only the sources
    /// tell the images apart.</summary>
    internal static InvalidOperationException OneOutfitImageCollision(string textureName, string outfit,
        string mesh, string first, string second) =>
        new($"stock texture '{textureName}' is retextured with two different images on {outfit}'s '{mesh}': "
            + $"'{first}' and '{second}'. One draw binds one image. Give both changes the same image");

    /// <summary>The refusal when two outfits ask one stock texture for different images at a mesh they BOTH
    /// draw: the presence gate is one verdict per frame, so it cannot separate two binds on a shared mesh
    /// and the later one would win for both. Names the shared mesh and both claimants; outfits with anchor
    /// meshes of their own each keep their image.</summary>
    internal static InvalidOperationException SharedAnchorImageCollision(string textureName, string mesh,
        string first, string second) =>
        new($"stock texture '{textureName}' is retextured with two different images on mesh '{mesh}': "
            + $"{first} and {second} both draw it, so one image would have to win. "
            + "Give both changes the same image");

    /// <summary>One draw-scoped retexture while the build accumulates it: the stock texture it overrides
    /// and one image per claiming outfit. <see cref="Claimant"/> names the edit that chose the mechanism,
    /// for <see cref="ImageCollision"/>; <see cref="MeshAt"/> maps each anchor's ib hash to the mesh it is,
    /// for <see cref="SharedAnchorImageCollision"/>.</summary>
    private sealed class ScopedBuild
    {
        public required string Name;
        public required string Hash;
        public required string TextureName;
        public required string Claimant;
        /// <summary>The part label of the FIRST claimant, for a refusal that has to name a change row.</summary>
        public required string Part;
        public readonly List<ScopedImage> Images = new();
        public readonly Dictionary<string, string> MeshAt = new(StringComparer.Ordinal);
    }

    /// <summary>One image under a <see cref="ScopedBuild"/>: what it binds, the key that gates it, and the
    /// anchors it binds at. <see cref="Seen"/> dedupes the anchors per (ib, latch); <see cref="OwnerAt"/>
    /// names the outfit that brought each anchor and <see cref="Source"/> the authored file, both for the
    /// anchor-exclusivity refusals.</summary>
    private sealed class ScopedImage
    {
        public required string Dds;
        public required string? Key;
        public required string Source;
        public readonly List<ScopedAnchor> Anchors = new();
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> OwnerAt = new(StringComparer.Ordinal);
    }

    /// <summary>What a mesh-dump dir holds, for the "one dump name means one mesh" rule: the mesh NAME and
    /// its index-buffer hash. NOT the bundle it was read from — two subjects wearing one asset reach it
    /// through their own bundles, and that is one mesh, not two.</summary>
    internal readonly record struct DumpIdentity(string MeshName, string IbHash);

    /// <summary>The mesh a capture claim stands on: the bundle it reads from, its name and its path id —
    /// the same triple an index buffer is hashed by, so one hash reached twice is one mesh exactly when
    /// the triples match.</summary>
    internal readonly record struct CaptureMesh(string Bundle, string MeshName, long PathId);

    /// <summary>Who holds an ib hash's one capture section: the mesh claiming it, and the label a refusal
    /// names that holder by.</summary>
    internal readonly record struct HashClaim(CaptureMesh Mesh, string Claimant);

    /// <summary>The refusal when <paramref name="dumpName"/> already means a different mesh than
    /// <paramref name="incoming"/>, else null. A silent dir reuse across different meshes would feed a
    /// pipeline foreign geometry, so identical content shares the dump and differing content refuses.</summary>
    internal static string? DumpNameConflict(string dumpName, DumpIdentity held, DumpIdentity incoming) =>
        held == incoming ? null
            : $"dump name '{dumpName}' maps to two different meshes ('{held.MeshName}' vs "
                + $"'{incoming.MeshName}'). Replaces whose subjects reuse a part name can't share one mod";

    /// <summary>The refusal when two Replaces land on ONE vanilla draw, else null. Two overrides on one
    /// hash fight over it and which wins is not something this build can decide — the same rule the install
    /// conflict read applies between mods. The test is the index-buffer HASH, so two subjects wearing one
    /// byte-identical mesh are caught where two same-named different meshes are not. Named per subject,
    /// because the author picked subjects, not hashes.</summary>
    internal static string? ReplacedMeshConflict(IEnumerable<(string Subject, string IbHash)> replaced)
    {
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (subject, hash) in replaced)
        {
            if (claimed.TryGetValue(hash, out var already))
                return $"'{already}' and '{subject}' replace one mesh they share. Two overrides on one "
                    + "hash fight over the draw. Drop one of the two Replaces";
            claimed[hash] = subject;
        }
        return null;
    }

    /// <summary>Fail the build on a blocked game asset. Don't weaken or drop the calls to this
    /// (<see cref="BuildBlacklist"/>).</summary>
    private static void RefuseBlocked(params string?[] names)
    {
        foreach (var n in names)
            if (BuildBlacklist.IsBlocked(n))
                throw new BlockedAssetException($"'{n}' is not a supported asset");
    }

    /// <summary>The encoded DDS an authored source of this sRGB family ships as: encoded on first claim,
    /// answered to every later claim of the same image (<paramref name="claimed"/> spans submeshes and
    /// pipelines). Identity is the source's CONTENT, never its name — the donor intake writes a copy per
    /// submesh, so a name-keyed claim would ship one painted image once per submesh. The sRGB family is in
    /// the identity because the tag is baked into the container. MAP KIND is deliberately NOT in the key:
    /// <see cref="AuthoredDds.Encode"/> applies no kind-dependent transform, so one image serving a normal
    /// and an RMO slot is one file, and sharing it is the point. <paramref name="onEncode"/> fires only on
    /// a real encode; a <c>.dds</c> source passes through untouched.</summary>
    internal static string EncodeOnce(Dictionary<string, string> claimed, string source, bool srgb,
        Func<string> newDest, Action onEncode,
        Action<string>? log, string? cacheDir, int? encoderCpuLimit)
    {
        string key = $"{AuthoredDds.SourceIdentity(source)}|{srgb}";
        if (claimed.TryGetValue(key, out var have)) return have;
        string dst = newDest();
        if (!AuthoredDds.IsPassthrough(source)) onEncode();
        AuthoredDds.Encode(source, dst, srgb, log, cacheDir, encoderCpuLimit);
        claimed[key] = dst;
        return dst;
    }

    /// <summary>The build-log line naming a texture encoder that is NOT the hardware device, so a build that
    /// took minutes per map says which rung it ran on; null for the hardware device. Log only: the encode
    /// is correct either way, so there is nothing for the author to fix.</summary>
    internal static string? EncoderRungLine(Bc7Encoder.Rung rung) => rung switch
    {
        Bc7Encoder.Rung.Hardware => null,
        Bc7Encoder.Rung.Warp => "texture encode: no GPU device, encoding on the WARP software renderer",
        _ => "texture encode: no graphics device, encoding on the managed encoder",
    };

    /// <summary>Warn per map kind some submesh binds whose anchor equivalent could not be slot-tagged: with
    /// no tag the bind has nothing to land on, and the geometry swaps wearing the anchor's own map. The
    /// wording separates the two binds because they cost the author differently: an authored map is work
    /// that won't show; a flat map on an untouched slot only fails to blank it.</summary>
    internal static void WarnUnbindableDonorMaps(IReadOnlyDictionary<int, SubmeshMaps> subMaps,
        IReadOnlyList<StockMapTag> stockMaps, string sfx, List<string> warnings)
    {
        void Warn(Func<SubmeshMaps, MapSlot> pick, StockMapKind kind, string name)
        {
            bool authored = subMaps.Values.Any(m => pick(m).File is not null);
            bool neutral = subMaps.Values.Any(m => pick(m).IsNeutral);
            if ((!authored && !neutral) || stockMaps.Any(t => t.Kind == kind)) return;
            warnings.Add($"donor ({sfx}): no anchor {name} could be slot-tagged, so the "
                + (authored ? $"donor {name}" : $"flat {name}")
                + " will not bind in game (the anchor's own map shows on the donor UVs)");
        }
        Warn(m => m.Albedo, StockMapKind.Albedo, "base color");
        Warn(m => m.Normal, StockMapKind.Normal, "normal");
        Warn(m => m.Rmo, StockMapKind.Rmo, "RMO");
    }

    /// <summary>The anchor's own stock maps, hashed offline and tagged with the kind whose slot they occupy,
    /// for the draw's slot probe. A map that won't hash warns with what it costs the author (the donor maps
    /// have nothing to bind through, so the anchor's own maps show); <paramref name="diagnostics"/> carries
    /// the reason. Tags come out in the anchor's material order, albedo then normal then RMO within each.
    /// <paramref name="partLabel"/> is the change-list label carried onto each tag, so an emitter refusal
    /// over one of these hashes can name its row.</summary>
    internal static List<StockMapTag> TagStockMaps(IReadOnlyList<Workbench.SubjectMaterial> anchorMaterials,
        string sfx, Func<Workbench.SubjectMap, string> hashOf, List<string> warnings, List<string> diagnostics,
        string partLabel = "")
    {
        var tags = new List<StockMapTag>();
        foreach (var material in anchorMaterials)
        {
            void Tag(Func<string, bool> isSlot, StockMapKind kind)
            {
                foreach (var m in material.Maps.Where(x => isSlot(x.Slot)))
                    try { tags.Add(new StockMapTag(hashOf(m), kind, partLabel)); }
                    catch (Exception ex) when (ex is not BlockedAssetException)
                    {
                        warnings.Add($"donor ({sfx}): anchor map '{m.TextureName}' can't be slot-tagged, "
                            + "so donor maps may not bind where it draws");
                        diagnostics.Add($"anchor map '{m.TextureName}' ({sfx}) can't be slot-tagged: {ex.Message}");
                    }
            }
            Tag(Materials.MaterialResolver.IsBaseColor, StockMapKind.Albedo);
            Tag(Materials.MaterialResolver.IsNormal, StockMapKind.Normal);
            Tag(Materials.MaterialResolver.IsRmo, StockMapKind.Rmo);
        }
        return tags;
    }

    public static Result Build(ModProject project, BuildEnv env, string outRoot,
        Action<string>? log = null, bool zip = true, BuildCaches? caches = null, int? encoderCpuLimit = null)
    {
        if (project.RootDir is null) throw new InvalidOperationException("project has no RootDir (save it first)");
        var warnings = new List<string>();
        var infos = new List<string>();
        var diagnostics = new List<string>();
        var edits = Workbench.VerbDerivation.Derive(project, env.ResolveSubject, warnings);
        if (edits.Count == 0)
            throw new InvalidOperationException("nothing to build. No edited meshes, edited textures, or hidden meshes");
        var dangling = edits
            .SelectMany(e => e.ReferencedFiles().Select(f => (Edit: e, File: f)))
            .Where(x => { try { return !File.Exists(project.Resolve(x.File)); } catch { return true; } })
            .ToList();
        if (dangling.Count > 0)
            throw new InvalidOperationException("edit files missing from the workspace: "
                + string.Join(", ", dangling.Select(d => $"{d.Edit.Mesh}: {d.File}")));
        // one published name for the folder and its zip; the transients sit beside them under the same root
        string packageName = ModNaming.PackageFolderName(project.Info);
        string workDir = Path.Combine(outRoot, $".work-{packageName}");
        string tmpMod = Path.Combine(outRoot, $".tmp-{packageName}");
        string finalDir = Path.Combine(outRoot, packageName);
        string zipPath = Path.Combine(outRoot, packageName + ".zip");

        // One reader for the build: it keeps each bundle parsed once, and every read below goes through it.
        var reader = new Bundles.BundleReader();
        var bundleCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[] Bundle(string id, string why)
        {
            if (!bundleCache.TryGetValue(id, out var bytes))
                bundleCache[id] = bytes = env.Deobfuscate(id);
            return bytes ?? throw new InvalidOperationException($"bundle '{id}' ({why}) isn't readable in this install");
        }

        try
        {
            foreach (var d in new[] { workDir, tmpMod })
            {
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                Directory.CreateDirectory(d);
            }

            // ---- resolve subjects and map each edit onto its live roster part -----------------------
            var subjects = new Dictionary<(string, string), SubjectModel>();
            SubjectModel Subject(string character, string stem)
            {
                var key = (character.ToLowerInvariant(), stem.ToLowerInvariant());
                if (subjects.TryGetValue(key, out var m)) return m;
                var model = env.ResolveSubject(character, stem)
                    ?? throw new InvalidOperationException($"subject '{character} · {stem}' didn't resolve. Re-check against the current game install");
                return subjects[key] = model;
            }

            var work = new List<(MeshEdit Edit, SubjectModel Model, SubjectPart Part)>();
            foreach (var e in edits)
            {
                var model = Subject(e.Character, e.Outfit);
                var part = model.Parts.FirstOrDefault(p =>
                        string.Equals(p.SlotName, e.Mesh, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"mesh '{e.Mesh}' is not in {e.Character} · {e.Outfit}'s roster (stale edit after a game update?)");
                RefuseBlocked(model.Character, model.Stem, part.SlotName, part.MeshAddress);
                work.Add((e, model, part));
            }

            // ---- toggle keys: tier 1 (the whole mod) + tier 2 (one change) ---------------------------
            // One key = one emitted variable, so two changes on the same key switch together. Sharing one is
            // the author's call to make; the emission is the same either way.
            string? modKey = ModKeys.Normalize(project.Info.ToggleKey);
            Project.ChangeKey? Binding(MeshEdit e) =>
                project.FindChangeKey(e.Character, e.Outfit, e.Mesh, e.Verb);
            string? ChangeKey(MeshEdit e) => ModKeys.Normalize(Binding(e)?.Key);
            // Off-meaning rides the key: without one the change ships unconditionally and has no off state.
            bool HidesWhenOff(MeshEdit e) => ChangeKey(e) is not null && Binding(e) is { HideWhenOff: true };

            // One key is ONE variable, so a shared key starts off only where every change on it asks to;
            // a disagreement takes the default of starting on. Order-independent, and the row's own ⚠
            // states the outcome. The mod's own key is a claimant that starts on, so a change sharing it
            // lands as any other disagreement.
            var keyStarts = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (modKey is not null) keyStarts[modKey] = false;
            foreach (var e in edits)
            {
                if (ChangeKey(e) is not { } k) continue;
                bool startsOff = Binding(e) is { StartsOff: true };
                keyStarts[k] = keyStarts.TryGetValue(k, out bool kept) ? kept && startsOff : startsOff;
            }
            var keysStartingOff = keyStarts.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

            // every tier of a part, forward-resolved: (mesh name, bundle id, path id)
            List<(string Name, string BundleId, long PathId)> Tiers(SubjectPart part)
            {
                var list = new List<(string, string, long)> { ResolveTier(part.SlotName, part.MeshAddress, part.MeshBundle, part.MeshPathId) };
                foreach (var t in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
                    list.Add(ResolveTier(t.SlotName, t.MeshAddress, t.MeshBundle, t.MeshPathId));
                return list;

                (string, string, long) ResolveTier(string name, string address, string? smrBundle, long smrPathId)
                {
                    RefuseBlocked(name, address);
                    if (!string.IsNullOrEmpty(smrBundle) && smrPathId != 0) return (name, smrBundle!, smrPathId);
                    if (string.IsNullOrEmpty(address))
                        throw new InvalidOperationException($"mesh '{name}' carries no recipe address and no resolved renderer mesh");
                    var owner = env.ResolveAddress(address)
                        ?? throw new InvalidOperationException($"no catalog entry for mesh address '{address}' (mesh '{name}')");
                    return (name, owner, 0);
                }
            }

            // Hashing an index buffer parses the mesh out of its bundle, and one mesh is asked for by the
            // pool, by its pipeline's captures and by the hide enumeration.
            var ibHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            string IbHash(string bundleId, string meshName, long pathId)
            {
                string key = $"{bundleId}|{meshName}|{pathId}";
                if (ibHashes.TryGetValue(key, out var have)) return have;
                return ibHashes[key] = BufferHash
                    .Compute(Bundle(bundleId, $"mesh '{meshName}'"), meshName, pathId, reader).Ib.ToString("x8");
            }

            // A mesh's stable identity for the operator cache: the game's own address for it, under the
            // catalog version that decides what that address resolves to. No catalog version means nothing
            // pins the contents, so the part is solved fresh.
            string? OpKey(string bundleId, string meshName, long pathId) =>
                env.CatalogVersion is { } cv ? $"{cv}|{bundleId}|{meshName}|{pathId}" : null;

            // ---- edit scope: the sharing measurement decides each edit's reach -----------------------
            // A measured shared TEXTURE goes draw-scoped instead of hash-global; a measured shared MESH
            // anchor gets the outfit's presence latch. No measurement (or an unmeasured subject) means
            // unscoped edits, said once rather than silently.
            var sharing = env.Sharing;
            if (sharing is null)
                diagnostics.Add("no sharing measurement: edits ship unscoped");
            // An outfit that failed to measure is uncovered, so a texture it shares reads private here and
            // rebinds game-wide: the reported reach is a floor rather than the truth. A build-log fact,
            // never a user-facing line — it names no action and reads as breakage on builds it cannot
            // even concern.
            else if (sharing.FailedOutfits.Count > 0)
                diagnostics.Add("sharing reach is a floor: unmeasured outfit(s) "
                    + string.Join(", ", sharing.FailedOutfits));
            var measuredNoted = new HashSet<(string, string)>();
            bool Measured(SubjectModel m)
            {
                if (sharing is null) return false;
                if (sharing.Covers(m.Character, m.Stem)) return true;
                if (measuredNoted.Add((m.Character, m.Stem)))
                    infos.Add($"asset sharing isn't measured for {m.Stem}. Its edits ship unscoped");
                return false;
            }
            IReadOnlyList<Workbench.SharingIndex.Wearer> MeshOthers(string ib, SubjectModel m) =>
                Measured(m) ? sharing!.MeshOtherWearers(ib, m.Character, m.Stem)
                            : Array.Empty<Workbench.SharingIndex.Wearer>();
            static string WearerLabels(IReadOnlyList<Workbench.SharingIndex.Wearer> wearers)
            {
                var labels = wearers.Select(w => w.CharacterLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return labels.Count <= 3 ? string.Join(", ", labels)
                    : $"{string.Join(", ", labels.Take(3))} and {labels.Count - 3} more";
            }

            // one presence latch per authored outfit that needs one; null = it has no private witness
            // mesh, so its shared-anchor edits ship ungated (disclosed once)
            var latchNames = new Dictionary<(string, string), string?>();
            var latchList = new List<WitnessLatch>();
            string? LatchFor(SubjectModel m)
            {
                var key = (m.Character.ToLowerInvariant(), m.Stem.ToLowerInvariant());
                if (latchNames.TryGetValue(key, out var have)) return have;
                var witnesses = sharing!.WitnessIbs(m.Character, m.Stem);
                if (witnesses.Count == 0)
                {
                    infos.Add($"{m.Stem} has no private mesh to witness its presence. "
                        + "Edits on its shared meshes apply wherever those meshes draw");
                    return latchNames[key] = null;
                }
                string name = PartName(m.Stem, "").TrimEnd('_');
                while (latchList.Any(l => l.Name == name)) name += "_";
                latchList.Add(new WitnessLatch(name, witnesses));
                return latchNames[key] = name;
            }

            // ---- the Replaces: one emitter pipeline each — pool, dumps, donor compile, Leaves, textures
            var pipelines = new List<ReplacePipeline>();
            var captureHashes = new Dictionary<string, string>();
            var allCaptureHashes = new HashSet<string>(StringComparer.Ordinal);   // every pipeline's lod0 + tier hashes
            // ib hash → the mesh holding its capture section, ACROSS pipelines: one capture section serves
            // one hash, and the emitter merges by hash. The SAME mesh reached by another Replace rides the
            // section already claimed for it — the case every Replace past the first on one outfit hits,
            // since their pools share the outfit's parts. Two DIFFERENT meshes on one hash refuse: they
            // would share a posed capture and pose each other's bones from whichever draw fired last.
            var poolHashOwner = new Dictionary<string, HashClaim>(StringComparer.Ordinal);
            var poolSlotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The slot names the author HID. A pooling pipeline suppresses only what it itself replaces —
            // its own target's vanilla draw, under its own gate — so another pipeline's replaced part keeps
            // running vanilla when this pipeline's key is off. A hidden part is different: the hide section
            // loop below leaves pooled slots to the capture sections, so every pooling pipeline suppresses
            // it, which is what makes the hide hold whichever pipelines are on.
            var hiddenMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (e, _, _) in work)
                if (e.Verb is EditVerbs.Hide) hiddenMeshes.Add(e.Mesh);

            // a part pooled by several Replaces dumps once; see DumpIdentity for what "once" means
            var dumpedParts = new Dictionary<string, DumpIdentity>(StringComparer.Ordinal);
            string ClaimDump(string dumpName, string meshName, string bid, long pid, string ibHash)
            {
                string dumpDir = Path.Combine(workDir, "dumps", dumpName);
                var incoming = new DumpIdentity(meshName, ibHash);
                if (dumpedParts.TryGetValue(dumpName, out var prev))
                {
                    if (DumpNameConflict(dumpName, prev, incoming) is { } conflict)
                        throw new InvalidOperationException(conflict);
                    return dumpDir;
                }
                StreamDump.Dump(Bundle(bid, $"pool part '{dumpName}'"), meshName, dumpDir, pid, reader);
                dumpedParts[dumpName] = incoming;
                return dumpDir;
            }

            // (replaced mesh, tagged stock hash) where some donor submesh INHERITS that kind — checked
            // against the scoped retextures once those are assembled, for the disclosure below
            var inheritingStockTags = new List<(string Mesh, string Hash)>();

            // pipeline order = roster order of the replaced part (the stable-order authority), so a
            // rebuild from the same edit list emits identically regardless of edit-list order
            var replaceWork = work.Where(w => w.Edit.Verb == EditVerbs.Replace)
                .OrderBy(w => w.Model.Character, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Model.Stem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Model.Parts.ToList().IndexOf(w.Part))
                .ToList();

            // Before any dumping or compiling: two Replaces on one vanilla draw can't both win, and the
            // refusal names the subjects the author picked rather than the dump name they collided on.
            if (ReplacedMeshConflict(replaceWork.Select(w =>
                {
                    var (name, bid, pid) = Tiers(w.Part)[0];
                    return ($"{w.Model.Character} · {w.Model.Stem} · {w.Part.Token}", IbHash(bid, name, pid));
                })) is { } clash)
                throw new InvalidOperationException(clash);

            // Each Replace takes the route its OWN target mesh admits. The MESH decides, not the renderer
            // class that drew it, so a skinned prop answers like any other skinned part and only a mesh with
            // no influences at all takes the rigid route — and a mesh neither route reaches is refused here,
            // before any donor is imported or any part dumped.
            var rigidWork = new List<(MeshEdit Edit, SubjectModel Model, SubjectPart Part)>();
            var pooledWork = new List<(MeshEdit Edit, SubjectModel Model, SubjectPart Part)>();
            foreach (var w in replaceWork)
            {
                var (name, bid, pid) = Tiers(w.Part)[0];
                var field = reader.GetMeshField(Bundle(bid, $"part '{w.Part.Token}'"), name, pid)
                    ?? throw new InvalidDataException($"mesh '{name}' not found in '{bid}'");
                switch (StreamDump.Route(field))
                {
                    case StreamDump.ReplaceRoute.Pooled: pooledWork.Add(w); break;
                    case StreamDump.ReplaceRoute.Rigid: rigidWork.Add(w); break;
                    default:
                        // a rigid layout took the branch above, so the only layout that reaches this is reduced
                        throw new InvalidOperationException($"'{w.Edit.Mesh}' can't be replaced: "
                            + $"{StreamDump.ReducedSkinReason(field)}. Drop this Replace");
                }
            }
            replaceWork = pooledWork;

            // hashing a stock texture reads and CRCs its whole mip 0, and one map is commonly bound
            // by several submeshes and subjects — do it once per texture. Shared by the Replace
            // slot tags below and the retexture overrides.
            var stockTextures = new Dictionary<string, StockTex>(StringComparer.Ordinal);
            // source content|srgb → the one encoded DDS, shared across submeshes and pipelines
            var donorTexEncoded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // noted on the first real ENCODE, so a build that only passes .dds sources through (or touches
            // no texture at all) says nothing about an encoder it never reached
            bool encoderNoted = false;
            void NoteEncoder()
            {
                if (encoderNoted) return;
                encoderNoted = true;
                if (EncoderRungLine(Bc7Encoder.Resolved) is { } line) diagnostics.Add(line);
            }

            // Donor textures, per donor submesh and per map slot. THE RULE, stated once for both replace
            // routes and driven by the slot's recorded origin rather than guessed from its siblings:
            //   Authored        → bind the encoded map at that submesh's draw
            //   ExplicitNeutral → bind the shipped flat map: the modder asked for the slot blanked
            //   VanillaOwn      → inherit, so the part's own real map keeps drawing
            //   None            → inherit, EXCEPT on a submesh that asked for something on another
            //                     slot, where normal/RMO take the flat map instead: that submesh draws
            //                     on donor UVs, and the anchor's real relief sampled through foreign
            //                     UVs reads as garbage. This is what protects a material-less or
            //                     hand-built donor. An unauthored albedo inherits either way — no flat
            //                     albedo exists to stand in for one.
            // A submesh with no row at all is untouched: every slot inherits.
            // The compiled donor UVs are Unity-convention like every other mesh the game samples,
            // so the maps flip on encode. Submeshes and replacements authored from the same image
            // share one shipped DDS (see EncodeOnce).
            //
            // <paramref name="anchor"/> is the part the donor maps bind AT: the pool's anchor on the pooled
            // route, and the replaced part itself on the rigid one, where the donor draws nowhere else.
            (Dictionary<int, SubmeshMaps> SubMaps, List<StockMapTag> StockMaps) DonorMaps(
                MeshEdit edit, SubjectPart anchor, string sfx, int submeshCount)
            {
                var subMaps = new Dictionary<int, SubmeshMaps>();
                bool AnchorFamily(Func<string, bool> isSlot, string kind, bool byConvention) =>
                    AnchorSrgb(anchor.Materials, isSlot, kind, byConvention, sfx,
                        m => StockTexture(m, edit.Mesh).Srgb, warnings, diagnostics);

                // one family per kind per replacement, resolved on FIRST USE — reading the anchor's stock
                // maps of a kind nobody authored would cost bundle reads and report disagreements about a
                // slot the build never binds through
                bool? albedoSrgb = null, normalSrgb = null, rmoSrgb = null;
                bool AlbedoSrgb() => albedoSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsBaseColor, "base color", byConvention: true);
                bool NormalSrgb() => normalSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsNormal, "normal", byConvention: false);
                bool RmoSrgb() => rmoSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsRmo, "RMO", byConvention: false);

                // sfx+submesh already separate the replacements and submeshes an image can reach, and a
                // replacement resolves one family per kind, so the two families never land on one name
                MapSlot Enc(string? rel, SlotOrigin ask, string kind, Func<bool> srgb, int submesh) => ask switch
                {
                    SlotOrigin.Authored when rel is not null =>
                        MapSlot.From(EncodeOnce(donorTexEncoded, project.Resolve(rel), srgb(),
                            () => Path.Combine(workDir, $"donor_{sfx}_s{submesh}_{kind}.dds"),
                            NoteEncoder, log, caches?.TextureDir, encoderCpuLimit)),
                    SlotOrigin.ExplicitNeutral => MapSlot.Neutral,
                    _ => MapSlot.Inherit,
                };
                foreach (var t in edit.Textures ?? new List<SubmeshTextures>())
                {
                    if (t.Submesh < 0 || t.Submesh >= submeshCount)
                        throw new InvalidOperationException(
                            $"Replace texture set on '{edit.Mesh}' targets donor submesh {t.Submesh}, but the donor has {submeshCount}");
                    if (subMaps.ContainsKey(t.Submesh))
                        throw new InvalidOperationException(
                            $"Replace on '{edit.Mesh}' carries two texture sets for donor submesh {t.Submesh}");
                    var albedo = Enc(t.Albedo, t.AlbedoAsk, "a", AlbedoSrgb, t.Submesh);
                    var normal = Enc(t.Normal, t.NormalAsk, "n", NormalSrgb, t.Submesh);
                    var rmo = Enc(t.Rmo, t.RmoAsk, "r", RmoSrgb, t.Submesh);
                    // The flat map goes wherever the shared rule says it does — the explicit blank, and the
                    // garbage relief a submesh drawing on donor UVs needs. Asked THROUGH that rule so the
                    // panes describing this emission read the same answer it binds. Albedo is not in it: no
                    // flat albedo exists, and an albedo asking for the neutral is refused by the emitter.
                    var flat = BlankedSlots.Of(t, EditVerbs.Replace);
                    if (flat.Normal) normal = MapSlot.Neutral;
                    if (flat.Rmo) rmo = MapSlot.Neutral;
                    subMaps[t.Submesh] = new SubmeshMaps(albedo, normal, rmo);
                }
                bool anyAuthored = subMaps.Values.Any(m => !m.BindsNothing);
                if (anyAuthored)
                    for (int i = 0; i < submeshCount; i++)
                    {
                        var m = subMaps.GetValueOrDefault(i);
                        var kept = new List<string>();
                        if (m is null || m.Albedo.IsInherit) kept.Add("base color");
                        if (m is null || m.Normal.IsInherit) kept.Add("normal");
                        if (m is null || m.Rmo.IsInherit) kept.Add("RMO");
                        if (kept.Count > 0)
                            diagnostics.Add($"donor ({sfx}) submesh {i}: no authored {string.Join("/", kept)}. "
                                + "It keeps the part's stock map there");
                    }

                // The anchor's stock map tags the draw's slot probe needs. Only a donor-textured replacement
                // needs them, and unhashable maps degrade rather than fail: the geometry still replaces.
                // A single map that won't hash warns per map, and a kind left with no tag at all warns
                // again — either way the donor maps don't bind in the draws that lost their tag.
                var stockMaps = new List<StockMapTag>();
                if (anyAuthored)
                {
                    stockMaps = TagStockMaps(anchor.Materials, sfx,
                        m => StockTexture(m, edit.Mesh).Hash, warnings, diagnostics, anchor.Token);
                    WarnUnbindableDonorMaps(subMaps, stockMaps, sfx, warnings);
                    // Remembered for the retexture phase: a tagged stock map that also gets a scoped
                    // retexture leaves this replacement's INHERITING submeshes on the pre-retexture image
                    // (the draw restores the slot from its save), which deserves a disclosure.
                    foreach (var tag in stockMaps)
                    {
                        bool inherits = Enumerable.Range(0, submeshCount).Any(i =>
                            subMaps.GetValueOrDefault(i) is not { } m || tag.Kind switch
                            {
                                StockMapKind.Albedo => m.Albedo.IsInherit,
                                StockMapKind.Normal => m.Normal.IsInherit,
                                _ => m.Rmo.IsInherit,
                            });
                        if (inherits) inheritingStockTags.Add((edit.Mesh, tag.Hash));
                    }
                }
                return (subMaps, stockMaps);
            }

            // The roster bone sets a pool derivation reads are a property of the SUBJECT: two Replaces on
            // one subject probe the same parts, and an unreadable part is one exclusion, not one per Replace.
            var rosterProbes = new Dictionary<(string, string),
                (List<PoolDerive.PartBones> Bones, Dictionary<string, SubjectPart> BySlot,
                 List<PoolDerive.MissingPart> HeldBack, Dictionary<string, System.Numerics.Matrix4x4?> Rests)>();
            (List<PoolDerive.PartBones> Bones, Dictionary<string, SubjectPart> BySlot,
             List<PoolDerive.MissingPart> HeldBack, Dictionary<string, System.Numerics.Matrix4x4?> Rests)
             RosterProbe(SubjectModel model)
            {
                var subjectKey = (model.Character.ToLowerInvariant(), model.Stem.ToLowerInvariant());
                if (rosterProbes.TryGetValue(subjectKey, out var have)) return have;

                // Readable parts only. Every part left out is kept with its reason, because a pool derived
                // over a SHORT roster fails in ways only the exclusions explain — and a Replace whose own
                // target is missing here has no pool question to ask at all.
                var bones = new List<PoolDerive.PartBones>();
                var bySlot = new Dictionary<string, SubjectPart>(StringComparer.OrdinalIgnoreCase);
                var heldBack = new List<PoolDerive.MissingPart>();
                var rests = new Dictionary<string, System.Numerics.Matrix4x4?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in model.Parts)
                {
                    RefuseBlocked(p.SlotName, p.MeshAddress);   // outside the try: the catch takes a diagnostic and carries on
                    try
                    {
                        var (name, bid, pid) = Tiers(p)[0];
                        var field = reader.GetMeshField(Bundle(bid, $"part '{p.Token}'"), name, pid)
                            ?? throw new InvalidDataException($"mesh '{name}' not found in '{bid}'");
                        // ahead of the skin rule: the bone table is what tells a later orphan-bone failure
                        // whether a refused part could have owned the bones the pool is missing
                        var hashes = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToHashSet();
                        if (StreamDump.UnrecoverableSkinReason(field) is { } why)
                        {
                            heldBack.Add(new PoolDerive.MissingPart(p.SlotName, why, hashes));
                            diagnostics.Add($"part '{p.Token}' excluded from pool derivation: can't feed palette recovery: {why}");
                            continue;
                        }
                        bones.Add(new PoolDerive.PartBones(p.SlotName, hashes,
                            Narrow: Mesh.SkinLayout.IsNarrow(field)));
                        bySlot[p.SlotName] = p;
                        // The part's measured scene rest rides the probe: a delta composed of two measured
                        // rests is how the union restates a part sharing too few bones with the anchor for
                        // a fitted delta. Best-effort — an unmeasurable rest leaves the fitted path, never
                        // holds the part back.
                        try
                        {
                            var skin = MeshSkin.Decode(field);
                            if (skin is { IsSkinned: true })
                                rests[p.SlotName] = (Skeleton.SceneRig.TryRead(Bundle(bid, $"part '{p.Token}'"), name, skin, pid)
                                    ?? (pid != 0 && model.PrimaryBundle.Length > 0
                                        ? Skeleton.SceneRig.TryReadForMeshRef(
                                            Bundle(model.PrimaryBundle, "assembly prefab"), pid, skin)
                                        : null))?.MeasuredRest;
                        }
                        catch { /* no rest to compose with — the fitted path decides */ }
                    }
                    catch (Exception ex) when (ex is not BlockedAssetException)
                    {
                        heldBack.Add(new PoolDerive.MissingPart(p.SlotName, ex.Message, BoneHashes: null));
                        diagnostics.Add($"part '{p.Token}' excluded from pool derivation: {ex.Message}");
                    }
                }
                return rosterProbes[subjectKey] = (bones, bySlot, heldBack, rests);
            }

            // What a part's LOD tiers ask of the union palette, and what the part can answer for another's:
            // the lod0 draw's capture hash and the bones it poses, then each renderable tier's hash and the
            // bones IT poses. Weighted rather than tabled on both sides — a bone with no weight behind it
            // moves no vertex of that draw, so it neither asks for a pool slot nor qualifies to supply one.
            // A tier the probe can't read is left out: the tier walk below degrades an unreadable tier to
            // its vanilla draw, so it captures nothing and asks for nothing, and the emitter's own per-tier
            // check backs the whole rule either way.
            var partTiers = new Dictionary<string, PoolDerive.PartTiers>(StringComparer.OrdinalIgnoreCase);
            PoolDerive.PartTiers TierBonesOf(SubjectModel model, string slotName)
            {
                string key = $"{model.Character}|{model.Stem}|{slotName}";
                if (partTiers.TryGetValue(key, out var have)) return have;
                var tiers = Tiers(RosterProbe(model).BySlot[slotName]);
                var list = new List<PoolDerive.TierBones>();
                for (int ti = 1; ti < tiers.Count; ti++)
                {
                    var (name, bid, pid) = tiers[ti];
                    if (Remold.Core.Model.MeshName.IsUnrenderedTier(name)) continue;
                    try
                    {
                        var field = reader.GetMeshField(Bundle(bid, $"tier '{name}'"), name, pid)
                            ?? throw new InvalidDataException($"mesh '{name}' not found in '{bid}'");
                        list.Add(new PoolDerive.TierBones(name, IbHash(bid, name, pid),
                            StreamDump.WeightedBoneHashes(field)));
                    }
                    catch (Exception ex) when (ex is not BlockedAssetException)
                    {
                        diagnostics.Add($"tier '{name}' left out of pool coverage: {ex.Message}");
                    }
                }
                var (l0Name, l0Bid, l0Pid) = tiers[0];
                // An unreadable lod0 skin costs the part only its candidacy as a carrier: the roster probe
                // already cleared the skin rule for every part that reaches here, so this is a read that
                // failed, not a layout that can't feed recovery.
                IReadOnlySet<uint> l0Weighted;
                try
                {
                    var l0Field = reader.GetMeshField(Bundle(l0Bid, $"part '{l0Name}'"), l0Name, l0Pid)
                        ?? throw new InvalidDataException($"mesh '{l0Name}' not found in '{l0Bid}'");
                    l0Weighted = StreamDump.WeightedBoneHashes(l0Field);
                }
                catch (Exception ex) when (ex is not BlockedAssetException)
                {
                    diagnostics.Add($"part '{l0Name}' can't cover another part's tier bones: {ex.Message}");
                    l0Weighted = new HashSet<uint>();
                }
                return partTiers[key] = new PoolDerive.PartTiers(
                    IbHash(l0Bid, l0Name, l0Pid), l0Weighted, list);
            }

            foreach (var (edit, model, part) in replaceWork)
            {
                string sfx = PartName(model.Character, part.Token);
                string donorAbs = project.Resolve(edit.DonorFile
                    ?? throw new InvalidOperationException($"Replace on '{edit.Mesh}' has no donor glb yet"));
                log?.Invoke($"donor ({sfx}): importing {Path.GetFileName(donorAbs)}");
                var payload = MeshGltf.ImportPayload(donorAbs, lenient: true);
                var recordedRest = Mesh.RestBake.FromList(edit.BakedRest, out bool restRefused);
                if (restRefused)
                    warnings.Add($"recorded rest pose on '{edit.Mesh}' is not an axis-aligned rotation. "
                        + "The donor compiles without it");

                var (partBones, partBySlot, heldBack, partRests) = RosterProbe(model);

                // The TARGET's own part being held back is answered here rather than left to fall out of pool
                // derivation, which would derive a pool the replaced part isn't in and anchor the pipeline
                // somewhere else. The skin rule cannot be the cause: a mesh no route admits is refused
                // before any donor is imported, so what reaches this is a roster probe that couldn't read
                // the part, and the message carries that read's own reason.
                if (heldBack.FirstOrDefault(m =>
                        string.Equals(m.Mesh, part.SlotName, StringComparison.OrdinalIgnoreCase)) is { } blocked)
                    throw new InvalidOperationException(
                        $"'{edit.Mesh}' can't be replaced: {blocked.Why}. Drop this Replace");

                // The roster this Replace may pool over. A one-influence part is in it only when it IS the
                // target, and what it is left out of is both halves at once — the derivation below and the
                // tier coverage after it read this one set.
                var (candidates, narrowOut) = PoolDerive.PoolCandidates(partBones, part.SlotName);
                if (narrowOut.Count > 0)
                    diagnostics.Add($"pool ({sfx}): one-influence part(s) left out: "
                        + string.Join(", ", narrowOut.Select(m => m.Mesh)));

                // The parts held back go WITH the roster: they are what tells an orphan-bone refusal apart
                // from a donor genuinely weighted to another armature.
                var derived = PoolDerive.Derive(payload, candidates, edit.AnchorOverride,
                    heldBack.Concat(narrowOut).ToList(), part.SlotName);

                // The pool the donor's weights ask for isn't always one the pool can POSE: a part's other
                // LOD tiers ride the union palette built from the pool's lod0 bone sets, and those tiers can
                // rig bones their own lod0 doesn't. The parts carrying them join here, ahead of every dump
                // and claim below, so they are set up exactly like the parts the donor pulled in — captured
                // for recovery, and left out of the suppression list further down.
                var pool = PoolDerive.CoverTierBones(derived, candidates, s => TierBonesOf(model, s),
                    MigotoEmitter.MaxPoolParts);
                // Build-log only: the extension is recovery bookkeeping the modder cannot act on, and the
                // shipped mod changes nothing about these parts.
                foreach (var added in pool.Pool.Except(derived.Pool, StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add($"'{partBySlot[added].SlotName}' is built alongside '{edit.Mesh}'. "
                        + "It is not changed");
                log?.Invoke($"pool ({sfx}): {string.Join(", ", pool.Pool)} (anchor {pool.Anchor})");

                // The workspace glb sits in scene-rest space, and where the union is stated is decided by
                // the anchor's measured rest — the same verdict the pool compile and the emitter read. A
                // scene-space union takes the payload's floats as exported; an anchor-space union takes
                // the recorded uprighting back off HERE — the one Unity-space boundary — so the compiled
                // payload lands in bind space. Exact inverse: an unedited round trip recovers the
                // original floats.
                if (!SwapCompile.TrySceneDelta(partRests.GetValueOrDefault(pool.Anchor), out _)
                    && recordedRest is { } uprighting)
                    payload = new MeshApply.Payload
                    {
                        Mesh = Mesh.RestBake.Unapply(payload.Mesh, uprighting),
                        JointIndices = payload.JointIndices,
                        JointWeights = payload.JointWeights,
                        SkinJointHashes = payload.SkinJointHashes,
                    };

                // dumps + capture hashes, in pool (roster) order
                var poolParts = new List<PoolPart>();
                var poolMeshes = new List<SwapCompile.PoolMesh>();
                var noSkip = new List<string>();
                var poolTiers = new List<PoolTier>();
                var pipelineHashes = new HashSet<string>(StringComparer.Ordinal);   // THIS pipeline's captures
                foreach (var s in pool.Pool) poolSlotNames.Add(s);
                foreach (var slotName in pool.Pool)
                {
                    var p = partBySlot[slotName];
                    string partName = PartName(model.Character, p.Token);
                    var (name, bid, pid) = Tiers(p)[0];
                    string h = IbHash(bid, name, pid);
                    // One capture section serves one hash. This part reached by another Replace's pool is
                    // the same mesh, so it rides the section already claimed for it. Two DIFFERENT meshes
                    // on one hash would point both parts' posed refs at whichever draw fired last — silent
                    // wrong geometry on animation. Refuse instead.
                    string claimant = $"{model.Character} · {model.Stem} · {p.Token}";
                    var claimed = new CaptureMesh(bid, name, pid);
                    if (poolHashOwner.TryGetValue(h, out var owner) && owner.Mesh != claimed)
                        throw new InvalidOperationException(
                            $"'{owner.Claimant}' and '{claimant}' share one draw signature. The swap can't capture "
                            + "them separately, so this Replace can't build");
                    poolHashOwner.TryAdd(h, new HashClaim(claimed, claimant));   // the first claimant names the refusal
                    string dumpDir = ClaimDump(partName, name, bid, pid, h);
                    poolParts.Add(new PoolPart(partName, dumpDir, OpKey(bid, name, pid),
                        partRests.GetValueOrDefault(slotName)));
                    poolMeshes.Add(new SwapCompile.PoolMesh(Bundle(bid, $"pool part '{p.Token}'"), name, pid,
                        partRests.GetValueOrDefault(slotName)));
                    captureHashes[partName] = h;
                    pipelineHashes.Add(h);
                    allCaptureHashes.Add(h);
                    // this pipeline's OWN target and the hidden parts are the only draws it suppresses;
                    // every other pool part it merely captures for recovery
                    bool ownTarget = string.Equals(slotName, part.SlotName, StringComparison.OrdinalIgnoreCase);
                    if (!ownTarget && !hiddenMeshes.Contains(slotName)) noSkip.Add(partName);
                }

                // Every pool part's other LOD tiers join the tier machinery. Suppressed parts' tiers are
                // REPLACED — LOD choice is not distance-only, so a hidden tier would blank the character in
                // every context that picks it. Leave parts' tiers are captured WITHOUT skip: in a frame that
                // renders only another tier the part's lod0 capture never fires, and an uncaptured recovery
                // input poses its owned bones with garbage. A tier that can't feed recovery, or shares a
                // content hash with an already-captured tier of this pipeline, is left running vanilla. A
                // tier whose hash ANOTHER pipeline already claimed refuses on the same terms as a pool
                // part: the emitter merges by hash, so one section would serve both pipelines' recoveries.
                foreach (var slotName in pool.Pool)
                {
                    var p = partBySlot[slotName];
                    string partName = PartName(model.Character, p.Token);
                    var tiers = Tiers(p);
                    for (int ti = 1; ti < tiers.Count; ti++)
                    {
                        var (name, bid, pid) = tiers[ti];
                        if (Remold.Core.Model.MeshName.IsUnrenderedTier(name)) continue;
                        string h = IbHash(bid, name, pid);
                        if (!pipelineHashes.Add(h)) continue;   // identical content rides the existing capture
                        // The tier suffix is the LOD label, read with MeshName.Lod for the same infix
                        // reason as the lodm0 check above: a variant tier like …_lod1_Fight is the lod1
                        // link of its chain, and the emitter pairs tiers across parts by this suffix.
                        string tierName = $"{partName}_{Remold.Core.Model.MeshName.Lod(name)}";
                        // Claimed only past the pipeline's own dedupe above, so a pipeline re-reaching a
                        // hash it already captured rides that capture instead of colliding with itself.
                        // Across pipelines the pool part's rule: the same tier mesh rides the section
                        // claimed for it, a DIFFERENT mesh on that hash refuses.
                        string tierClaimant = $"{model.Character} · {model.Stem} · {tierName}";
                        var tierClaimed = new CaptureMesh(bid, name, pid);
                        if (poolHashOwner.TryGetValue(h, out var tierOwner) && tierOwner.Mesh != tierClaimed)
                            throw new InvalidOperationException(
                                $"'{tierOwner.Claimant}' and '{tierClaimant}' share one draw signature. The swap can't capture "
                                + "them separately, so this Replace can't build");
                        bool mintedClaim = poolHashOwner.TryAdd(h, new HashClaim(tierClaimed, tierClaimant));
                        // the same rule ClaimDump claims on, ahead of the degrade-to-warning catch below:
                        // a tier name meaning two different meshes is a refusal, not a warning
                        if (dumpedParts.TryGetValue(tierName, out var prevTier)
                            && DumpNameConflict(tierName, prevTier, new DumpIdentity(name, h)) is { } tierClash)
                            throw new InvalidOperationException(tierClash);
                        string dumpDir;
                        try { dumpDir = ClaimDump(tierName, name, bid, pid, h); }
                        catch (Exception ex)
                        {
                            pipelineHashes.Remove(h);
                            // No capture on it, so the hash is free for another pipeline — but only a claim
                            // minted here may be withdrawn: a ridden claim's capture is another pipeline's,
                            // and that dump already succeeded under a name of its own.
                            if (mintedClaim) poolHashOwner.Remove(h);
                            warnings.Add($"tier '{name}' can't serve the swap ({ex.Message}). Its vanilla draw is left running");
                            continue;
                        }
                        poolTiers.Add(new PoolTier(partName, tierName, Remold.Core.Model.MeshName.Lod(name), dumpDir, h,
                            OpKey(bid, name, pid)));
                        allCaptureHashes.Add(h);
                    }
                }

                log?.Invoke($"donor ({sfx}): compiling onto the union bone order");
                string donorDir = Path.Combine(workDir, $"donor_{sfx}");
                // layout target = the ANCHOR: the compiled streams bind at the anchor's draw, whose input
                // layout expects that part's exact strides/formats — a hair-anchored pipeline conformed to
                // the body's narrower stream1 reads garbage UVs
                int anchorIdx = -1;
                for (int i = 0; i < pool.Pool.Count; i++)
                    if (string.Equals(pool.Pool[i], pool.Anchor, StringComparison.OrdinalIgnoreCase)) { anchorIdx = i; break; }
                if (anchorIdx < 0)
                    throw new InvalidOperationException($"anchor '{pool.Anchor}' is not in its own pool ({string.Join(", ", pool.Pool)})");
                var compiled = SwapCompile.CompilePool(poolMeshes, donorAbs, donorDir, anchorIdx, payload, reader);
                warnings.AddRange(compiled.Warnings);
                diagnostics.AddRange(compiled.Diagnostics);

                var (subMaps, stockMaps) = DonorMaps(edit, partBySlot[pool.Anchor], sfx, compiled.SubmeshCount);

                // presence latch when other outfits also draw any of this pipeline's meshes: the
                // suppression and the donor draw then apply only while this outfit is on screen
                string? pipeLatch = null;
                var pipeOthers = pipelineHashes.SelectMany(h => MeshOthers(h, model))
                    .Distinct().ToList();
                if (pipeOthers.Count > 0 && (pipeLatch = LatchFor(model)) is not null)
                {
                    var cross = pipeOthers.Where(w =>
                        !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (cross.Count > 0)
                        infos.Add($"'{edit.Mesh}' rides meshes also drawn by {WearerLabels(cross)}. "
                            + $"The replacement applies while {model.Stem} is on screen");
                }

                pipelines.Add(new ReplacePipeline
                {
                    Suffix = sfx,
                    Parts = poolParts,
                    DonorDir = donorDir,
                    CaptureHashes = captureHashes,
                    Anchor = PartName(model.Character, partBySlot[pool.Anchor].Token),
                    SubTextures = subMaps.Count > 0 ? subMaps : null,
                    NoSkipParts = noSkip.Count > 0 ? noSkip : null,
                    Tiers = poolTiers.Count > 0 ? poolTiers : null,
                    StockMaps = stockMaps.Count > 0 ? stockMaps : null,
                    ToggleKey = ChangeKey(edit),
                    HideWhenOff = HidesWhenOff(edit),
                    Latch = pipeLatch,
                });
            }

            // ---- the rigid Replaces: compile onto the target's own layout and swap the buffers ---------
            // No pool, no dumps, no palette: the draw is not posed per vertex, so the compiled streams are
            // what the vanilla ones were and the section binds them under the same draw.
            var rigids = new List<RigidReplace>();
            foreach (var (edit, model, part) in rigidWork)
            {
                string sfx = PartName(model.Character, part.Token);
                string donorAbs = project.Resolve(edit.DonorFile
                    ?? throw new InvalidOperationException($"Replace on '{edit.Mesh}' has no donor glb yet"));
                log?.Invoke($"donor ({sfx}): importing {Path.GetFileName(donorAbs)}");
                var payload = MeshGltf.ImportPayload(donorAbs, lenient: true);
                var recordedRest = Mesh.RestBake.FromList(edit.BakedRest, out bool restRefused);
                if (restRefused)
                    warnings.Add($"recorded rest pose on '{edit.Mesh}' is not an axis-aligned rotation. "
                        + "The donor compiles without it");
                // The workspace glb sits in scene-rest space and the target's own space is where the swap
                // lands, so the recorded uprighting comes back off here. There is no pool to state a shared
                // space for, which is what makes this unconditional rather than an anchor's verdict.
                if (recordedRest is { } uprighting)
                    payload = new MeshApply.Payload
                    {
                        Mesh = Mesh.RestBake.Unapply(payload.Mesh, uprighting),
                        JointIndices = payload.JointIndices,
                        JointWeights = payload.JointWeights,
                        SkinJointHashes = payload.SkinJointHashes,
                    };

                var (name, bid, pid) = Tiers(part)[0];
                log?.Invoke($"donor ({sfx}): compiling onto the part's own layout");
                string rigidDir = Path.Combine(workDir, $"rigid_{sfx}");
                var compiled = SwapCompile.CompilePart(Bundle(bid, $"part '{part.Token}'"), name, donorAbs,
                    rigidDir, pid, payload, reader);
                warnings.AddRange(compiled.Warnings);
                diagnostics.AddRange(compiled.Diagnostics);

                // the donor binds at this part's own draws and nowhere else, so the part IS the anchor
                var (subMaps, stockMaps) = DonorMaps(edit, part, sfx, compiled.SubmeshCount);

                // Every renderable tier of the part: its own hash first, then the siblings. All of them are
                // suppressed and redrawn, since a tier left alone would show the stock mesh wherever the
                // game picks it.
                string ownHash = IbHash(bid, name, pid);
                var tierHashes = new List<string>();
                var claimed = new HashSet<string>(StringComparer.Ordinal) { ownHash };
                foreach (var (tName, tBid, tPid) in Tiers(part).Skip(1))
                {
                    if (Remold.Core.Model.MeshName.IsUnrenderedTier(tName)) continue;
                    string h = IbHash(tBid, tName, tPid);
                    if (claimed.Add(h)) tierHashes.Add(h);   // identical content rides the section already there
                }
                foreach (var h in claimed) allCaptureHashes.Add(h);   // the hide pass leaves these sections alone

                // presence latch when other outfits also draw this part: the suppression and the donor draw
                // then apply only while this outfit is on screen. Walked in emission order, so the
                // disclosure a rebuild writes reads the same way twice.
                string? rigidLatch = null;
                var rigidOthers = new[] { ownHash }.Concat(tierHashes)
                    .SelectMany(h => MeshOthers(h, model)).Distinct().ToList();
                if (rigidOthers.Count > 0 && (rigidLatch = LatchFor(model)) is not null)
                {
                    var cross = rigidOthers.Where(w =>
                        !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (cross.Count > 0)
                        infos.Add($"'{edit.Mesh}' rides meshes also drawn by {WearerLabels(cross)}. "
                            + $"The replacement applies while {model.Stem} is on screen");
                }

                diagnostics.Add($"'{edit.Mesh}' took the rigid replace route ({compiled.SubmeshCount} submesh(es))");

                rigids.Add(new RigidReplace
                {
                    Suffix = sfx,
                    DonorDir = rigidDir,
                    Hash = ownHash,
                    TierHashes = tierHashes.Count > 0 ? tierHashes : null,
                    SubTextures = subMaps.Count > 0 ? subMaps : null,
                    StockMaps = stockMaps.Count > 0 ? stockMaps : null,
                    ToggleKey = ChangeKey(edit),
                    HideWhenOff = HidesWhenOff(edit),
                    Latch = rigidLatch,
                });
            }

            // ---- hides: every shipped tier of every suppressed NON-pool mesh. Suppressed pool parts'
            // tiers are captures (replaced draws), never hides. A pooled part hidden by an edit of its own
            // goes the same way, whether the donor's weights pooled it or tier coverage did: it is
            // suppressed inside every pipeline that pools it, under those pipelines' gates rather than its
            // own hide gate. Hide-when-off wins a shared pooled part — one draw can't be both suppressed
            // and captured, and the capture is what poses the swap. ----
            var hides = new List<string>();
            var hideSeen = new HashSet<string>(StringComparer.Ordinal);
            // one hide hash can be reached by two edits (same mesh, two subjects): the FIRST edit to claim
            // the hash owns its toggle key, matching the hash-dedup right above it, and a second claimant
            // on a different key is named rather than dropped
            var hideKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            var hideLatches = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (e, model, part) in work)
            {
                if (e.Verb is not (EditVerbs.Replace or EditVerbs.Hide)) continue;
                if (poolSlotNames.Contains(part.SlotName)) continue;
                foreach (var (name, bid, pid) in Tiers(part))
                {
                    if (Remold.Core.Model.MeshName.IsUnrenderedTier(name)) continue;
                    string h = IbHash(bid, name, pid);
                    if (allCaptureHashes.Contains(h)) continue;
                    if (!hideSeen.Add(h))
                    {
                        if (HideKeyCollisionWarning(name, hideKeys.GetValueOrDefault(h), ChangeKey(e)) is { } w)
                            warnings.Add(w);
                        continue;
                    }
                    hides.Add(h);
                    if (ChangeKey(e) is { } hk) hideKeys[h] = hk;
                    var others = MeshOthers(h, model);
                    if (others.Count > 0 && LatchFor(model) is { } latch)
                    {
                        hideLatches[h] = latch;
                        // disclosure only where someone ELSE's model visibly co-changes; the same doll's
                        // other outfits are never on screen with this one outside a mirror
                        var cross = others.Where(w =>
                            !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (cross.Count > 0)
                            infos.Add($"'{name}' is also drawn by {WearerLabels(cross)}. "
                                + $"The hide applies while {model.Stem} is on screen");
                    }
                }
            }

            // ---- retextures: one override per STOCK TEXTURE, keyed on its own resource hash ----------
            // The identity is the texture, not the mesh or submesh: the override rebinds that resource
            // wherever it is sampled. Two submeshes, parts or subjects sharing one map collapse to a single
            // section. Two DIFFERENT images on one stock texture are expressible only where the mechanism is
            // draw-scoped AND the claims sit on anchor meshes of their own; the game-wide rebind has one
            // image to give, and two claims meeting on one anchor have one draw to give.
            var retex = new List<RetexEntry>();
            var retexByHash = new Dictionary<string, string>(StringComparer.Ordinal);   // texture hash → its DDS
            var retexKeyByHash = new Dictionary<string, string>(StringComparer.Ordinal); // texture hash → its toggle key
            var retexClaimant = new Dictionary<string, string>(StringComparer.Ordinal);  // texture hash → the edit that claimed it
            // draw-scoped retextures (shared stock textures), by stock hash: one entry per stock texture
            // carrying one image per claiming outfit, whose anchors accumulate across the parts and
            // subjects that bind the texture
            var scopedBuild = new List<ScopedBuild>();
            var scopedIdx = new Dictionary<string, int>(StringComparer.Ordinal);        // stock hash → scopedBuild index
            var crossAnchorNoted = new HashSet<string>(StringComparer.Ordinal);
            var rtxEncoded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // content|srgb → dst
            var rtxDstOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // dst → source
            var rtxContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // source path → content sha
            string RtxContent(string resolved) => rtxContent.TryGetValue(resolved, out var sha) ? sha
                : rtxContent[resolved] = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(resolved)));
            foreach (var (e, model, part) in work)
            {
                if (e.Verb != EditVerbs.Retexture) continue;
                if (e.Textures is not { Count: > 0 })
                    throw new InvalidOperationException($"Retexture on '{e.Mesh}' has no texture sets yet");
                string partName = PartName(model.Character, part.Token);
                if (part.Materials.Count == 0)
                    throw new InvalidOperationException(
                        $"Retexture on '{e.Mesh}': the part's renderer binds no material, so there is no stock texture to override");
                foreach (var t in e.Textures)
                {
                    if (t.Submesh < 0)
                        throw new InvalidOperationException($"Retexture on '{e.Mesh}' targets submesh {t.Submesh}");
                    // renderer m_Materials order IS the submesh binding; a shortfall repeats the last
                    // slot, the same rule the preview assigns maps by
                    var material = part.Materials[Math.Min(t.Submesh, part.Materials.Count - 1)];

                    string Enc(string src, string kind, bool srgb)
                    {
                        // one encode per (source CONTENT, sRGB family) — EncodeOnce's rule: two outfits
                        // given the same image collapse to one shipped file (and pass the same-image
                        // check below)
                        string resolved = project.Resolve(src);
                        string key = $"{RtxContent(resolved)}|{srgb}";
                        if (rtxEncoded.TryGetValue(key, out var have)) return have;
                        string stem = $"rtx_{Path.GetFileNameWithoutExtension(resolved)}_{kind}";
                        string dst = Path.Combine(workDir, stem + ".dds");
                        // the first family of a source keeps the plain name (the common case, and the one
                        // that has to stay stable across rebuilds); a second family is suffixed with it
                        if (rtxDstOwner.TryGetValue(dst, out var owner)
                            && string.Equals(owner, resolved, StringComparison.OrdinalIgnoreCase))
                            dst = Path.Combine(workDir, $"{stem}_{(srgb ? "srgb" : "lin")}.dds");
                        if (rtxDstOwner.TryGetValue(dst, out var other)
                            && !string.Equals(other, resolved, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                $"retexture sources '{other}' and '{resolved}' share a file name; the build can't ship both");
                        rtxDstOwner[dst] = resolved;
                        // a passthrough never reaches the encoder, so it says nothing about one either
                        if (!AuthoredDds.IsPassthrough(resolved)) NoteEncoder();
                        AuthoredDds.Encode(resolved, dst, srgb, log, caches?.TextureDir, encoderCpuLimit);
                        rtxEncoded[key] = dst;
                        return dst;
                    }

                    void Map(string? authored, string kind, Func<string, bool> isSlot)
                    {
                        if (authored is null) return;
                        var stock = material.Maps.FirstOrDefault(m => isSlot(m.Slot));
                        if (stock is null)
                        {
                            warnings.Add($"retexture '{e.Mesh}' submesh {t.Submesh}: material '{material.Name}' binds no "
                                + $"{kind} map, so the authored one has nothing to override. Skipped");
                            return;
                        }
                        // the replacement inherits the stock texture's sRGB family: same resource, same slot
                        var info = StockTexture(stock, e.Mesh);
                        string hash = info.Hash;
                        string dds = Enc(authored, kind, info.Srgb);
                        string claimant = $"'{e.Mesh}' on {model.Stem}";
                        // the sharing measurement decides the mechanism: a private texture rebinds
                        // hash-global (cheapest, covers every pass and LOD); a shared one rebinds at
                        // this subject's own mesh draws so no other wearer repaints
                        bool scopedRoute = Measured(model)
                            && sharing!.TexOtherWearers(hash, model.Character, model.Stem).Count > 0;

                        if (scopedIdx.TryGetValue(hash, out int si))
                        {
                            // the scoped section carries one image per claiming outfit, so a second
                            // outfit's DIFFERENT image is a per-outfit disambiguation rather than a
                            // conflict. It has to be scoped too: the game-wide rebind this claim would
                            // otherwise take cannot live under the anchors' section.
                            var entry = scopedBuild[si];
                            if (!scopedRoute
                                && !entry.Images.Any(i => string.Equals(i.Dds, dds, StringComparison.OrdinalIgnoreCase)))
                                throw ImageCollision(stock.TextureName, entry.Claimant, claimant);
                            AddScopedAnchors(entry, dds, ChangeKey(e));
                            return;
                        }
                        if (retexByHash.TryGetValue(hash, out var have))
                        {
                            // same stock texture reached twice on the game-wide route: an identical
                            // replacement collapses to one section, a different one has nowhere to go
                            if (!string.Equals(have, dds, StringComparison.OrdinalIgnoreCase))
                                throw ImageCollision(stock.TextureName, retexClaimant[hash], claimant);
                            // the collapse leaves ONE section, so it can carry only one key — say which
                            if (!ModKeys.SameKey(retexKeyByHash.GetValueOrDefault(hash), ChangeKey(e))
                                && (retexKeyByHash.ContainsKey(hash) || ChangeKey(e) is not null))
                                warnings.Add($"stock texture '{stock.TextureName}' is retextured by two changes with "
                                    + "different toggle keys. One section can carry one key, so "
                                    + $"{(retexKeyByHash.TryGetValue(hash, out var kept) ? kept : "no key")} applies. "
                                    + "Set the same key on both");
                            return;
                        }
                        if (!scopedRoute)
                        {
                            retexByHash[hash] = dds;
                            retexClaimant[hash] = claimant;
                            if (ChangeKey(e) is { } rk) retexKeyByHash[hash] = rk;
                            retex.Add(new RetexEntry($"{partName}_{kind}_{hash}", hash, dds, ChangeKey(e)));
                            return;
                        }
                        scopedIdx[hash] = scopedBuild.Count;
                        var fresh = new ScopedBuild
                        {
                            Name = $"{partName}_{kind}_{hash}", Hash = hash,
                            TextureName = stock.TextureName, Claimant = claimant, Part = part.Token,
                        };
                        scopedBuild.Add(fresh);
                        AddScopedAnchors(fresh, dds, ChangeKey(e));

                        void AddScopedAnchors(ScopedBuild entry, string image, string? key)
                        {
                            // identity is (image, key): one section carries every claiming outfit's bind,
                            // so two outfits asking for the same image on different keys stay separable
                            // rather than collapsing onto whichever key was seen first
                            var img = entry.Images.FirstOrDefault(i =>
                                string.Equals(i.Dds, image, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(i.Key, key, StringComparison.Ordinal));
                            if (img is null)
                                entry.Images.Add(img = new ScopedImage
                                {
                                    Dds = image, Key = key, Source = Path.GetFileName(authored),
                                });
                            foreach (var (name, bid, pid) in Tiers(part))
                            {
                                if (Remold.Core.Model.MeshName.IsUnrenderedTier(name)) continue;
                                string ib = IbHash(bid, name, pid);
                                var others = MeshOthers(ib, model);
                                string? latch = others.Count > 0 ? LatchFor(model) : null;
                                entry.MeshAt[ib] = name;
                                // dedupe per (anchor, latch): several of this mod's outfits legitimately
                                // claim one shared anchor, and each contributes its OWN gated bind — the
                                // emitted OR is what makes the edit follow every claiming outfit
                                if (!img.Seen.Add($"{ib}|{latch}")) continue;
                                img.OwnerAt.TryAdd(ib, model.Stem);
                                img.Anchors.Add(new ScopedAnchor(ib,
                                    $"{partName}_{Remold.Core.Model.MeshName.Lod(name)}", latch));
                                // disclosure only where someone ELSE's model visibly co-changes
                                var cross = others.Where(w =>
                                    !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                                if (latch is not null && cross.Count > 0
                                    && crossAnchorNoted.Add($"{entry.Hash}|{model.Stem}"))
                                    infos.Add($"'{stock.TextureName}' rides a mesh shared with "
                                        + $"{WearerLabels(cross)}. While {model.Stem} is on screen, theirs "
                                        + "shows this edit too");
                            }
                        }
                    }

                    Map(t.Albedo, "a", Materials.MaterialResolver.IsBaseColor);
                    Map(t.Normal, "n", Materials.MaterialResolver.IsNormal);
                    Map(t.Rmo, "r", Materials.MaterialResolver.IsRmo);
                }
            }
            // one anchor mesh, one image. The gate that separates two claims is a whole-frame verdict, so
            // where their anchors MEET it cannot pick between their binds and the later write wins for
            // both; claims on anchor meshes of their own stay separable and each keeps its image.
            foreach (var b in scopedBuild)
            {
                var claimed = new Dictionary<string, ScopedImage>(StringComparer.Ordinal);
                foreach (var img in b.Images)
                    foreach (var a in img.Anchors)
                    {
                        if (!claimed.TryGetValue(a.Hash, out var held)) { claimed[a.Hash] = img; continue; }
                        if (string.Equals(held.Dds, img.Dds, StringComparison.OrdinalIgnoreCase)) continue;
                        string first = held.OwnerAt[a.Hash], second = img.OwnerAt[a.Hash];
                        throw string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                            ? OneOutfitImageCollision(b.TextureName, first, b.MeshAt[a.Hash],
                                held.Source, img.Source)
                            : SharedAnchorImageCollision(b.TextureName, b.MeshAt[a.Hash], first, second);
                    }
            }
            var scopedRetex = scopedBuild
                .Select(b => new ScopedRetexEntry(b.Name, b.Hash,
                    b.Images.Select(i => new ScopedRetexImage(i.Dds, i.Anchors, i.Key)).ToList(), b.Part))
                .ToList();

            StockTex StockTexture(SubjectMap stock, string mesh)
            {
                RefuseBlocked(stock.TextureName);
                string key = $"{stock.BundleId}|{stock.TextureName}";
                if (stockTextures.TryGetValue(key, out var cached)) return cached;
                var src = reader.GetTextureHashSource(
                        Bundle(stock.BundleId, $"stock texture '{stock.TextureName}'"), stock.TextureName)
                    ?? throw new InvalidDataException(
                        $"stock texture '{stock.TextureName}' (retexture on '{mesh}') isn't in bundle '{stock.BundleId}'");
                uint dxgi = TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)src.Format, src.Srgb)
                    ?? throw new InvalidOperationException(
                        $"stock texture '{stock.TextureName}' is in a format this build can't hash offline "
                        + $"(Unity format {src.Format})");
                return stockTextures[key] = new StockTex(
                    TextureHash.Compute(src.PictureData, src.Width, src.Height, src.MipCount, dxgi).ToString("x8"),
                    TextureHash.IsSrgb(dxgi));
            }

            // A scoped retexture on a stock map the Replace probe also tags: authored donor maps still
            // bind (the probe accepts the retexture's tag), but inheriting submeshes draw from the
            // slot's SAVE, taken before the scoped rebind — the pre-retexture image.
            if (scopedRetex.Count > 0 && inheritingStockTags.Count > 0)
            {
                var scopedStock = scopedRetex.Select(s => s.StockHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (mesh, hash) in inheritingStockTags.Where(t => scopedStock.Contains(t.Hash))
                             .DistinctBy(t => $"{t.Mesh}|{t.Hash}", StringComparer.OrdinalIgnoreCase))
                    infos.Add($"'{mesh}' inherits a stock map that is also retextured. "
                        + "Submeshes without an authored map keep the stock image");
            }

            // ---- emit -------------------------------------------------------------------------------
            log?.Invoke("final assembly: operators, buffers, ini");
            MigotoEmitter.Result emitted;
            var emitter = new MigotoEmitter { OperatorCacheDir = caches?.OperatorDir };
            if (pipelines.Count > 0 || rigids.Count > 0)
            {
                emitted = emitter.Build(new PoolBuildRequest
                {
                    Pipelines = pipelines,
                    Rigids = rigids.Count > 0 ? rigids : null,
                    OutDir = tmpMod,
                    HideHashes = hides.Count > 0 ? hides : null,
                    Retextures = retex.Count > 0 ? retex : null,
                    ScopedRetextures = scopedRetex.Count > 0 ? scopedRetex : null,
                    ToggleKey = modKey,
                    HideKeys = hideKeys.Count > 0 ? hideKeys : null,
                    HideLatches = hideLatches.Count > 0 ? hideLatches : null,
                    Latches = latchList.Count > 0 ? latchList : null,
                    KeysStartingOff = keysStartingOff.Count > 0 ? keysStartingOff : null,
                });
            }
            else
            {
                emitted = emitter.BuildOverlaysOnly(tmpMod, retex, hides, modKey,
                    hideKeys.Count > 0 ? hideKeys : null,
                    scopedRetex.Count > 0 ? scopedRetex : null,
                    latchList.Count > 0 ? latchList : null,
                    hideLatches.Count > 0 ? hideLatches : null,
                    keysStartingOff.Count > 0 ? keysStartingOff : null);
            }
            warnings.AddRange(emitted.Warnings);
            diagnostics.AddRange(emitted.Diagnostics);

            WriteSidecar(project, env, tmpMod, work,
                captureHashes.Values.Concat(rigids.SelectMany(r => r.Hashes)), hides, retex,
                scopedRetex, latchList);

            // ---- swap into place, then the distribution zip -----------------------------------------
            // The previous build is renamed ASIDE, never deleted in place: a single locked file under it
            // fails a recursive delete halfway, which would leave the author with neither the build they
            // had nor the one they asked for. A failed publish puts the aside back.
            // An aside whose delete failed on an EARLIER build (a file was locked then) is swept here,
            // where the lock has usually let go — otherwise nothing ever reclaims it.
            try
            {
                foreach (var stale in Directory.GetDirectories(
                             Path.GetDirectoryName(finalDir)!, Path.GetFileName(finalDir) + ".old-*"))
                    try { Directory.Delete(stale, recursive: true); } catch { /* still locked — next build retries */ }
            }
            catch { /* parent unreadable — the publish below reports the real problem */ }
            string? aside = null;
            if (Directory.Exists(finalDir))
            {
                aside = finalDir + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(finalDir, aside);
            }
            try { Directory.Move(tmpMod, finalDir); }
            catch
            {
                if (aside is not null) Directory.Move(aside, finalDir);
                throw;
            }
            if (aside is not null)
                try { Directory.Delete(aside, recursive: true); }
                catch { /* the superseded build is regenerable; a leftover aside costs only disk */ }
            string? builtZip = null;
            if (zip)
            {
                PublishDistributionZip(finalDir, zipPath);
                builtZip = zipPath;
            }
            log?.Invoke($"built: {finalDir}");
            return new Result(finalDir, builtZip, warnings, infos, diagnostics);
        }
        finally
        {
            foreach (var d in new[] { workDir, tmpMod })
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
        }
    }

    /// <summary>Write the distribution zip aside and move it onto <paramref name="zipPath"/>, so the
    /// published name only ever holds a complete archive: a write that dies partway (an unreadable source
    /// file) would otherwise leave a truncated zip under the real name beside a sound mod folder. The temp
    /// is swept on failure.</summary>
    internal static void PublishDistributionZip(string modDir, string zipPath)
    {
        string tmp = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDistributionZip(modDir, tmp);
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>The distribution zip: every file under <paramref name="modDir"/>, nested under the folder's
    /// own name so an extract lands one mod folder. Encoded textures are already block-compressed, so they
    /// are STORED; everything else (ini, hlsl, json, the operator and geometry buffers) is deflated.
    /// <b>Reproducible:</b> entry order is fixed and every entry's timestamp is pinned to
    /// <see cref="ZipEntryStamp"/>, so the same mod folder always zips to the same bytes.</summary>
    private static void WriteDistributionZip(string modDir, string zipPath)
    {
        string baseName = Path.GetFileName(modDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string entry = baseName + "/" + Path.GetRelativePath(modDir, file).Replace(Path.DirectorySeparatorChar, '/');
            // Built by hand rather than CreateEntryFromFile, which stamps the source file's mtime and seals
            // the entry against a later correction.
            var e = archive.CreateEntry(entry,
                file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            e.LastWriteTime = ZipEntryStamp;
            using var src = File.OpenRead(file);
            using var dst = e.Open();
            src.CopyTo(dst);
        }
    }

    /// <summary>The fixed timestamp every distribution-zip entry carries, so two builds of identical inputs
    /// produce identical bytes. Any constant would do as long as it is at or after 1980-01-01, which is the
    /// earliest the zip format's DOS time field can express.</summary>
    private static readonly DateTimeOffset ZipEntryStamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Short part name for ini section names and filenames, which both eat it: lowercase character
    /// + part token, with every character that is not a letter or digit collapsed to <c>_</c>. Letters and
    /// digits are judged by Unicode, so a non-ASCII name keeps its own characters.</summary>
    internal static string PartName(string character, string token)
    {
        var sb = new StringBuilder();
        foreach (char c in $"{character}_{token}".ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    /// <summary>The <c>gf2mod.json</c> sidecar (schema frozen): identity, provenance, the override hash list
    /// a mod manager can predict conflicts from, and build-time versions. Stock 3DMigoto ignores it. It
    /// carries no timestamp, so identical inputs write an identical sidecar.</summary>
    private static void WriteSidecar(ModProject project, BuildEnv env, string modDir,
        IReadOnlyList<(MeshEdit Edit, SubjectModel Model, SubjectPart Part)> work,
        IEnumerable<string> captureHashes, IEnumerable<string> hides, IEnumerable<RetexEntry> retex,
        IEnumerable<ScopedRetexEntry> scopedRetex, IEnumerable<WitnessLatch> latches)
    {
        string? preview = null;
        if (project.Info.Preview is { } prev)
        {
            var src = project.Resolve(prev);
            if (File.Exists(src))
            {
                preview = "preview" + Path.GetExtension(src).ToLowerInvariant();
                File.Copy(src, Path.Combine(modDir, preview), overwrite: true);
            }
        }

        var subjects = work.Select(w => (w.Model.Character, w.Model.Stem)).Distinct().ToList();
        var doc = new
        {
            schema = 1,
            name = project.Info.Name,
            version = project.Info.Version,
            author = project.Info.Author,
            description = project.Info.Description,
            preview,
            character = project.Info.Character ?? (subjects.Count == 1 ? subjects[0].Character : null),
            outfit = project.Info.Outfit,
            source_outfit = subjects.Count == 1 ? subjects[0].Stem : null,
            override_hashes = captureHashes.Concat(hides).Concat(retex.Select(r => r.Hash))
                .Concat(scopedRetex.SelectMany(s => s.Images
                    .SelectMany(i => i.Anchors.Select(a => a.Hash)).Append(s.StockHash)))
                .Concat(latches.SelectMany(l => l.WitnessIbs))
                .Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToArray(),
            game_catalog = env.CatalogVersion,
            app_version = env.AppVersion,
        };
        File.WriteAllText(Path.Combine(modDir, "gf2mod.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
