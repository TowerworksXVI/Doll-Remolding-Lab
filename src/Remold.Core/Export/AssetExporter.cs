using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
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
/// (<see cref="Mesh.RestBake"/>), undone at package build; null = nothing recorded, and the export
/// then bakes the scene rig's own uprighting (<see cref="Mesh.RestBake.Effective"/>).
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
/// Exports a part's high-detail (lod0) mesh + its textures to a mod working folder for Blender
/// (<see cref="BuildRiggedGlbs"/>): meshes to <c>meshes/</c> as <c>.glb</c>, textures to
/// <c>textures/</c> as <c>.png</c>. Textures resolve renderer-first
/// (<see cref="Textures.PartTextureResolver"/>); a part whose renderer binds no texture reports the miss
/// loudly and exports the mesh untextured.
/// </summary>
public static class AssetExporter
{
    // v3: the outbound texture inventory records each material position's drawability and folds every
    // primitive onto a drawable material, so a cached part's record must be the one this build writes.
    // v4: a part with no recorded rest is baked by its scene rig's own uprighting, and the record beside
    // the glb says so.
    // v5: no exported rest world carries a reflection, and duplicate faces ship on split vertex copies so
    // Blender keeps them.
    public const string RiggedBuildSpec = "rigged-build-spec-v5";

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
    /// is classified against. Null roster at the export means candidacy is unknown and the whole subject
    /// skeleton is offered, the behaviour before this filter existed.
    ///
    /// <para>A null <paramref name="Scheme"/> is not the harmless end of anything. It means either "this
    /// subject is not modular" — where nothing is lost, since no <c>P&lt;n&gt;_</c> token needs classifying
    /// — or "the tables would not read", where every modular token classifies as an unknown variant, no
    /// sibling can vouch for another, coverage returns nothing, and the tail NARROWS to each part's own
    /// posed bones. That is the under-offer direction: bones a build would have accepted paint on are not
    /// offered at all. The roster cannot tell the two apart; the caller that read the tables can, and the
    /// app's open says so on its status line.</para></summary>
    public sealed record SubjectRoster(IReadOnlyList<RosterPart> Parts,
        IReadOnlyList<Tables.PartScheme.Slot>? Scheme = null,
        bool PartsPoolAlone = false);

    /// <summary>One stock texture a rigged build actually addressed, using the durable stock-PNG cache's
    /// own key plus the run-local filename written into the GLB sidecar.</summary>
    public readonly record struct StockTextureDependency(string BundleContentId, string TextureName,
        long PathId, string DestinationFileName);

    /// <summary>Structured facts observed by one rigged build. They are output state rather than assumptions
    /// at the open call site: cache publication reads these flags and the exact bundles and stock textures
    /// this invocation touched.</summary>
    public sealed class RiggedBuildDiagnostics
    {
        private readonly HashSet<string> _bundleReads = new(StringComparer.Ordinal);
        private readonly HashSet<string> _requiredBundleReads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StockTextureDependency> _stockTextures =
            new(StringComparer.OrdinalIgnoreCase);
        private int _hadTransientFailures;

        public bool Completed { get; private set; }
        public bool WasCanceled { get; private set; }
        public bool HadProjectAuthoredContent { get; private set; }
        public bool ProducedComposition { get; private set; }
        /// <summary>Whether every build input came from the game-side route. A composition is a product
        /// shape, not authored content; <see cref="ProducedComposition"/> lets per-part and combined cache
        /// publishers require the product shape they own.</summary>
        public bool GameSideOnly => !HadProjectAuthoredContent;
        public bool HadTransientFailures => System.Threading.Volatile.Read(ref _hadTransientFailures) != 0;
        public IReadOnlyList<string> BundleReads =>
            _bundleReads.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        public IReadOnlyList<string> RequiredBundleReads =>
            _requiredBundleReads.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        public IReadOnlyList<StockTextureDependency> StockTextures =>
            _stockTextures.Values.OrderBy(value => value.DestinationFileName, StringComparer.Ordinal).ToArray();

        internal void ObserveInputs(
            IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut,
                IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
            string? combinedOut,
            IReadOnlyDictionary<string, IReadOnlyList<(string? Base, string? Normal, string? Rmo)>>? authoredMaps,
            IReadOnlyDictionary<string, IReadOnlyList<TextureTransportOverride>>? authoredTextureMaps,
            IReadOnlyCollection<string>? observedGameSidePreparedGlbs)
        {
            ProducedComposition |= combinedOut is not null;
            var gamePrepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (observedGameSidePreparedGlbs is not null)
                foreach (string path in observedGameSidePreparedGlbs)
                    try { gamePrepared.Add(Path.GetFullPath(path)); } catch { /* malformed cannot certify */ }
            bool IsObservedGamePrepared(string path)
            {
                try { return gamePrepared.Contains(Path.GetFullPath(path)); }
                catch { return false; }
            }
            HadProjectAuthoredContent |= parts.Any(part => part.BakedRest is not null
                    || part.EditedGlb is { } edited && !IsObservedGamePrepared(edited))
                || authoredMaps is { Count: > 0 }
                || authoredTextureMaps is { Count: > 0 };
        }

        internal void ObserveBundle(string logical, bool required)
        {
            if (string.IsNullOrWhiteSpace(logical)) return;
            _bundleReads.Add(logical);
            if (required) _requiredBundleReads.Add(logical);
        }

        internal void ObserveStockTexture(string contentId, string name, long pathId, string destination)
        {
            if (string.IsNullOrWhiteSpace(contentId) || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(destination)) return;
            _stockTextures[destination] = new StockTextureDependency(contentId, name, pathId, destination);
        }

        internal void TransientFailure() =>
            System.Threading.Interlocked.Exchange(ref _hadTransientFailures, 1);
        internal void Canceled() => WasCanceled = true;
        internal void Complete() => Completed = true;
    }

    /// <summary>The canonical app-side half of a rig-cache identity. It includes every roster and build-spec
    /// value that can change a per-part rig, but never run-directory paths. List order is preserved where the
    /// exporter preserves it; strings are length-prefixed so no game-produced delimiter can alias another
    /// shape.</summary>
    public static string RiggedBuildFingerprint(Outfit outfit, string character, SubjectRoster? roster,
        IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut,
            IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
        bool wardrobeUnreadable)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var number = new byte[8];
        void Number(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(number, value);
            hash.AppendData(number);
        }
        void Flag(bool value) => Number(value ? 1 : 0);
        void Text(string? value)
        {
            if (value is null) { Number(-1); return; }
            var bytes = Encoding.UTF8.GetBytes(value);
            Number(bytes.Length);
            hash.AppendData(bytes);
        }

        Text(RiggedBuildSpec);
        Number(outfit.ModelConfigId);
        Text(outfit.Stem);
        Number((int)outfit.Kind);
        Text(character);
        Flag(wardrobeUnreadable);

        Number(parts.Count);
        foreach (var part in parts)
        {
            Text(part.Part);
            Text(part.SourceBundle);
            Text(part.MeshName);
            Flag(part.GlbOut is not null);
            Number(part.PathId);
            Flag(part.EditedGlb is not null);
            if (part.BakedRest is null) Number(-1);
            else
            {
                Number(part.BakedRest.Count);
                foreach (var value in part.BakedRest) Number(BitConverter.SingleToInt32Bits(value));
            }
        }

        Flag(roster is not null);
        if (roster is not null)
        {
            Flag(roster.PartsPoolAlone);
            Number(roster.Parts.Count);
            foreach (var part in roster.Parts)
            {
                Text(part.Mesh);
                Text(part.Token);
                Text(part.SourceBundle);
                Number(part.PathId);
                Flag(part.CastsShadows);
                Number((int)part.Visibility);
            }
            if (roster.Scheme is null) Number(-1);
            else
            {
                Number(roster.Scheme.Count);
                foreach (var slot in roster.Scheme)
                {
                    Number(slot.Id);
                    Number(slot.Variants.Count);
                    foreach (var variant in slot.Variants)
                    {
                        Number(variant.Id);
                        Flag(variant.IsDefault);
                        Number(variant.Tokens.Count);
                        foreach (var token in variant.Tokens) Text(token);
                    }
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The <c>rosterDegraded</c> entry a rigged build adds when an export fell back to offering the
    /// WHOLE skeleton because candidacy was unknown for it — no row of the roster measured, or the exported
    /// part absent from the rows that did. Parenthesised so it can never collide with a slot name, which is
    /// what every other entry in that collection is.</summary>
    internal const string RosterUnfiltered = "(candidacy unknown)";

    /// <summary>Why a roster row that produced no candidacy is held back, for the wardrobe-coverage rule
    /// that reads the held-back list. It never reaches a modder: the build states its own reasons, one per
    /// part, and this route only decides whether a slot may certify coverage.</summary>
    private const string RosterUnmeasured = "its mesh or its weights couldn't be read";

    /// <summary>Filename a session's Blender send lands under — what the open declares to the bridge as the
    /// session file's <c>sendAs</c>, on every route that writes one. Distinct from the glb the session was
    /// opened on (<see cref="CombinedGlbName"/>, or the run folder's composition) so a send never writes
    /// over it: that file's map record is what classifies the send's own images.</summary>
    public const string SessionSendGlbName = "return.glb";

    /// <summary>
    /// Lazy open-in-Blender upgrade: rebuild the RIGGED Blender-facing glb(s) for already-exported parts —
    /// the named/posed armature + JOINTS/WEIGHTS + per-submesh preview material the Add skips, since the rig
    /// is ~the entire Add cost and is only needed when the modder actually opens Blender. Each mesh is read
    /// from the SAME bundle the Add recorded, so the geometry is byte-identical to the shipped glb.
    ///
    /// <para>The part's renderer-bound maps are put in <paramref name="texDir"/> here, at the seam every open
    /// route passes: the resolver already names each map and its owning bundle, and the run's own
    /// deobfuscation cache already holds that bundle's bytes (the material walk read them to get the names),
    /// so a map costs a decode and a PNG encode at worst and a hard link at best — see
    /// <see cref="StockTextureCache"/>. The bone-name table is
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
    /// is the one axis of the tail a key over this build's own inputs cannot pin: everything else candidacy
    /// reads is a pure function of the catalog version and the workspace stamps, so a row that measured
    /// unmeasurable measures unmeasurable on every rerun and its tail would be cacheable. A row listed HERE
    /// may read differently the moment the lock clears, so a caller caching the result must not keep it.
    /// Nothing caches a build today — this is the contract for one that would.</param>
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
    /// <param name="authoredMaps">Per part token, the modder's OWN map files for that part, indexed as
    /// <see cref="OverlayAuthoredMaps"/> indexes them — an entry overrides the stock PNG in its slot. Null
    /// (or a part with no entry) leaves that part on its stock maps alone. This is the combined route's half
    /// of what <see cref="MeshGltf.ReexportPartGlb"/> does for a lone part; without it an open-all session
    /// shows the game texture under the modder's own painted one. Nothing is MARKED authored in the combined
    /// glb's own record: a send back is classified against the part's own prepared glb, which the lone
    /// re-export marks, and a mark written beside the combined file would be read by nothing.</param>
    /// <param name="stockTextureCacheRoot">Where the full-resolution stock PNGs this open needs are kept
    /// between runs (<see cref="StockTextureCache"/>), or null for no persistence: every map decoded and
    /// encoded afresh, nothing left behind. The app passes <see cref="LabPaths.StockTextureRoot"/>.</param>
    /// <param name="unreadableTextures">Receives the NAME of every map this run could not put in front of the
    /// glb writer — an unreadable bundle, a texture the bundle doesn't hold, or a file that turned out not to
    /// decode as a picture (which also drops its cache entry, so the next open re-exports it). A map of the
    /// modder's own lands here under its file name, since nothing else names it. Those material positions
    /// open untextured, so a caller with a status surface says so once for the run. Deliberately data rather
    /// than log lines: the two are one object in the app, and a line per texture is the flashing the
    /// aggregate replaces.</param>
    public static IReadOnlyList<string> BuildRiggedGlbs(string anyGamePath, GameVfs vfs,
        Outfit outfit, string character, IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut, IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
        string texDir, IProgress<string>? log = null, string? combinedOut = null,
        ICollection<string>? vanillaFallbacks = null, SubjectRoster? roster = null,
        ICollection<string>? rosterDegraded = null, string? candidacyCacheFile = null,
        CancellationToken ct = default, ICollection<string>? rosterUnreadable = null,
        IReadOnlyDictionary<string, IReadOnlyList<(string? Base, string? Normal, string? Rmo)>>? authoredMaps = null,
        string? stockTextureCacheRoot = null, ICollection<string>? unreadableTextures = null,
        IReadOnlyDictionary<string, IReadOnlyList<TextureTransportOverride>>? authoredTextureMaps = null,
        bool reportBlenderTexCoordWarnings = false,
        RiggedBuildDiagnostics? diagnostics = null,
        IReadOnlyCollection<string>? observedGameSidePreparedGlbs = null,
        PreviewBlobMemo? previewMemo = null)
    {
        try
        {
            var built = BuildRiggedGlbsCore(anyGamePath, vfs, outfit, character, parts, texDir, log, combinedOut,
                vanillaFallbacks, roster, rosterDegraded, new CandidacyCache(candidacyCacheFile), ct,
                rosterUnreadable, authoredMaps,
                stockTextureCacheRoot is null ? null : new StockTextureCache(stockTextureCacheRoot),
                unreadableTextures, authoredTextureMaps, reportBlenderTexCoordWarnings, diagnostics,
                observedGameSidePreparedGlbs, previewMemo);
            diagnostics?.Complete();
            return built;
        }
        catch (OperationCanceledException)
        {
            diagnostics?.Canceled();
            throw;
        }
    }

    /// <summary>The body of <see cref="BuildRiggedGlbs"/> on a <see cref="CandidacyCache"/> the caller
    /// owns — the seam the candidacy pass's cost is measured through, since what a run had to read and
    /// scan is otherwise invisible from outside.</summary>
    internal static IReadOnlyList<string> BuildRiggedGlbsCore(string anyGamePath, GameVfs vfs,
        Outfit outfit, string character, IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut, IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> parts,
        string texDir, IProgress<string>? log, string? combinedOut,
        ICollection<string>? vanillaFallbacks, SubjectRoster? roster,
        ICollection<string>? rosterDegraded, CandidacyCache cache, CancellationToken ct,
        ICollection<string>? rosterUnreadable = null,
        IReadOnlyDictionary<string, IReadOnlyList<(string? Base, string? Normal, string? Rmo)>>? authoredMaps = null,
        StockTextureCache? textureCache = null, ICollection<string>? unreadableTextures = null,
        IReadOnlyDictionary<string, IReadOnlyList<TextureTransportOverride>>? authoredTextureMaps = null,
        bool reportBlenderTexCoordWarnings = false,
        RiggedBuildDiagnostics? diagnostics = null,
        IReadOnlyCollection<string>? observedGameSidePreparedGlbs = null,
        PreviewBlobMemo? previewMemo = null)
    {
        diagnostics?.ObserveInputs(parts, combinedOut, authoredMaps, authoredTextureMaps,
            observedGameSidePreparedGlbs);
        // the subject-resolution surface resolves these to nothing; this entry point takes the subject
        // directly and answers the same way (ExportBlacklist's contract: empty, no visible trace)
        if (ExportBlacklist.IsBlocked(character) || ExportBlacklist.IsBlocked(outfit.Stem))
            return Array.Empty<string>();
        var subjectSlug = ModNaming.SubjectSlug(character, outfit.Stem);
        var reader = new BundleReader();
        var warnedTexCoordParts = reportBlenderTexCoordWarnings
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        // the glbs written here are the ones opened in Blender, so the flat maps have to be on disk beside
        // the stock maps before their sidecars are written
        PreviewMaps.WriteNeutrals(texDir);
        var decCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[]? Dec(string logical, bool required = true)
        {
            diagnostics?.ObserveBundle(logical, required);
            if (decCache.TryGetValue(logical, out var cached)) return cached;
            byte[]? bytes;
            // IOException (sharing violation = game running) PROPAGATES so the shell's BUSY catch offers
            // "close the game and retry"; swallowing it degrades a game-locked read into empty rig output
            try { bytes = vfs.TryDeobfuscateLogical(logical); }
            catch (IOException) { diagnostics?.TransientFailure(); throw; }
            catch { diagnostics?.TransientFailure(); bytes = null; }
            if (bytes is null) diagnostics?.TransientFailure();
            return decCache[logical] = bytes;
        }
        byte[]? RequiredDec(string logical) => Dec(logical, required: true);
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
            try { return Dec(logical, required: false); }
            catch (IOException)
            {
                // deliberately NOT cached as null: another part off the same bundle, one this run exports,
                // has to reach the rethrow rather than inherit this degrade
                if (lockedForSkeleton.Add(logical))
                    log?.Report($"Bones missing for {part}: the game is using its files.");
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
        string? ContentIdOf(string logical)
        {
            if (contentIds.TryGetValue(logical, out var known)) return known;
            string? id;
            try
            {
                id = vfs.Catalog.BundleNameToInternalId.TryGetValue(logical, out var internalId)
                    && vfs.Manifest.TryLocate(internalId, out var located)
                    ? Convert.ToHexString(located.Stub.SubHash).ToLowerInvariant()
                    : null;
            }
            catch { id = null; }
            return contentIds[logical] = id;
        }
        string? CandidacyKey(string logical, string meshName, long pathId)
        {
            diagnostics?.ObserveBundle(logical, required: false);
            if (!cache.Enabled) return null;
            var id = ContentIdOf(logical);
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
        var scope = Workbench.SubjectScope.Build(vfs.Catalog, RequiredDec, outfit);

        // ---- the run's texture folder -------------------------------------------------------------------
        //
        // Put every renderer-bound map the glbs embed on disk under the name ResolvePartPngs looks it up by.
        // This is the ONLY producer on the Blender-open route: without it the folder holds the two flat
        // neutrals and nothing else, every material resolves no image, and the glb ships one material for the
        // whole part.
        //
        // The cost the design turns on: reading and de-obfuscating the owning bundle is already paid — the
        // material walk above opened it through this run's own `Dec` memo just to read the texture names — so
        // what a map costs here is a BCn decode and a PNG encode, and nothing else. That pair is ~95% of a
        // cold open and is why the pictures are kept between runs (StockTextureCache) and, on a miss,
        // decoded across a few threads at once.
        var placedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missedTextures = new HashSet<string>(StringComparer.Ordinal);
        // Every run-folder map this pass names, by the path the glb writers read it under: the texture it
        // holds and the durable entry that answers for it. Filled on the export thread and read only by the
        // glb writes, which are sequential too. It is what lets a picture that turns out not to BE a picture
        // take its own cache entry down with it — see MapWouldNotDecode.
        var workspaceMaps = new Dictionary<string, (string? ContentId, string Name, long PathId)>(
            StringComparer.OrdinalIgnoreCase);
        var workspaceSrgb = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        void EnsureWorkspacePngs(PartTextures partTex)
        {
            // Every ordinary Texture2D can ride the property-keyed carrier even when the approximation has no
            // honest PBR socket for it. Toon ramps are float lookup data, not ordinary pictures; flattening
            // those values to this 8-bit PNG path would be a lossy representation.
            var wanted = new List<(string Name, string Dest, byte[] Bundle, string? ContentId, long PathId)>();
            foreach (var t in partTex.All)
            {
                if (t.IsRamp || t.Bundle is null) continue;
                var dest = Path.Combine(texDir, WorkspaceTextureName(partTex, t, subjectSlug));
                if (!placedTextures.Add(dest)) continue;   // a sibling part of this run shares the map
                var contentId = ContentIdOf(t.Bundle);
                if (contentId is not null)
                    diagnostics?.ObserveStockTexture(contentId, t.Name, t.PathId, Path.GetFileName(dest));
                else
                    diagnostics?.TransientFailure(); // no durable stock key means a later rig hit cannot re-home it
                // Recorded before any of the early-outs below: a file another run of this open already put
                // there is still one whose entry has to be droppable when it turns out not to decode.
                workspaceMaps[dest] = (contentId, t.Name, t.PathId);
                var bundle = Dec(t.Bundle);
                if (bundle is null) { missedTextures.Add(t.Name); continue; }
                try
                {
                    workspaceSrgb[dest] = reader.GetTextureSrgb(bundle,
                        new BundleReader.TextureRef(t.Name, t.PathId));
                }
                catch { workspaceSrgb[dest] = null; }
                // Already on disk — the open's per-part build and its combined build share one folder, and
                // the second must not decode what the first put there.
                if (File.Exists(dest)) continue;
                // A cache hit is a link, which costs nothing worth parallelizing — take it inline.
                if (contentId is not null && textureCache?.TryGet(contentId, t.Name, t.PathId) is { } cached
                    && StockTextureCache.Place(cached, dest))
                    continue;
                // The bytes are in the run's memo already (see above); reading them HERE keeps every
                // dictionary touch on this thread and leaves the parallel pass pure decode + encode.
                wanted.Add((t.Name, dest, bundle, contentId, t.PathId));
            }
            if (wanted.Count == 0) return;
            // Bounded: the decode+encode pair is CPU-bound and each worker holds a full-resolution image, so
            // a 2048² map is tens of megabytes in flight per thread. Half the machine, at most four.
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                CancellationToken = ct,
            };
            Parallel.ForEach(wanted, options, item =>
            {
                // Every failure here costs ONE map and names it: a texture the bundle no longer holds, a
                // format or a blob the decoder refuses, a file that wouldn't write. The alternative — letting
                // it out — fails the whole open over one picture, and the modder's geometry is what they came
                // for. Cancellation is not a texture failure and goes back to the loop.
                try
                {
                    // A reader per worker: the shared one is not thread-safe, and minting one is free.
                    // Selected by the PATH ID the renderer pinned: a bundle can hold several same-named
                    // Texture2Ds and a name-selected read takes whichever comes first, which is another
                    // texture's pixels under this one's name.
                    var decoded = new BundleReader().GetTexture(item.Bundle,
                        new BundleReader.TextureRef(item.Name, item.PathId));
                    if (decoded is null)
                    {
                        diagnostics?.TransientFailure();
                        lock (missedTextures) missedTextures.Add(item.Name);
                        return;
                    }
                    // Publish to the cache first, then link: the cached file is the one every later open
                    // reads, and a cache that couldn't take it still leaves this run its own copy.
                    if (item.ContentId is not null
                        && textureCache?.Publish(decoded.Value, item.ContentId, item.Name, item.PathId) is { } cached
                        && StockTextureCache.Place(cached, item.Dest))
                        return;
                    TextureExport.WritePng(decoded.Value, item.Dest);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    diagnostics?.TransientFailure();
                    lock (missedTextures) missedTextures.Add(item.Name);
                }
            });
        }

        // A picture that will not decode costs exactly its own map. The glb writers are what meet the
        // failure — they are the only things that decode these files — and this pass is the only thing that
        // knows where the bytes came from, so the two meet here: the texture is named to the caller, the
        // durable entry that served it and the run copy linked from it are deleted, and the next open
        // exports the map from the game again. Without the delete an entry damaged INSIDE its PNG envelope
        // (the cache's whole-file check reads the signature and the IEND, not the middle) is served to every
        // later open of every subject that binds that texture, forever.
        //
        // A path this pass never named is the modder's OWN map or a shipped neutral: named to the caller
        // too, since the material opens without it either way, but never deleted — it is not this app's file
        // and there is no entry behind it.
        void MapWouldNotDecode(string pngPath)
        {
            diagnostics?.TransientFailure();
            if (!workspaceMaps.TryGetValue(pngPath, out var map))
            {
                lock (missedTextures) missedTextures.Add(Path.GetFileNameWithoutExtension(pngPath));
                return;
            }
            lock (missedTextures) missedTextures.Add(map.Name);
            if (map.ContentId is not null) textureCache?.Invalidate(map.ContentId, map.Name, map.PathId);
            try { File.Delete(pngPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next open retries */ }
        }

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
                // scene rig for NAMES/parenting and for the part's own uprighting. The mesh-bundle read
                // comes first (classes whose own bundle carries the scene), selected by the path id where
                // there is one — the recorded name is the slot's and need not name the mesh object.
                // smr-body parts fall back to the assembly prefab, keyed by the slot's mesh reference.
                // Read ONCE per part: an edited part needs it too, for the map and for its connectors.
                SceneRig? sceneRig = null;
                if (skin is { IsSkinned: true })
                    sceneRig = SceneRig.TryRead(dec, meshName, skin, pathId)
                        ?? (pathId != 0 && scope.Candidates.Count > 0
                            ? SceneRig.TryReadForMeshRef(scope.Candidates[0].Dec, pathId, skin)
                            : null);
                // The space this part's workspace sits in: the rest its project record states where one
                // exists (a converted 0.3.x project), else the scene rig's own uprighting — so a body
                // that ships lying down stands up in Blender whether or not anything was ever recorded
                // for it, and the build un-bakes by the same rule. A recorded rest that is not an
                // axis-aligned rotation cannot be un-baked by transpose, so it is refused and the part
                // lands in bind space rather than a skewed one.
                var partRest = RestBake.Effective(bakedRest, sceneRig?.Uprighting, out bool restRefused);
                // the rig's paths are in GAME bone order, so they pair with this part's game skin
                if (skin is { IsSkinned: true }) unionParts.Add((skin, sceneRig?.BonePaths, partRest));
                // A part this run writes no glb for has already given what it was read for — its share of
                // the subject's skeleton. Decoding its geometry and resolving its textures would buy
                // nothing, so a rig-only part costs one bundle read rather than a whole part export.
                if (glbOut is null && combinedOut is null) continue;
                if (restRefused)
                    log?.Report($"{part} opens in bind pose: its rest pose can't be applied.");
                // Named by the RECORDED name, which is the renderer slot's; on some enemy/prop slots the
                // mesh asset's own m_Name differs, and the send-back joins its parts to the ledger by the
                // name the glb carries.
                var mesh = UnityMesh.Decode(field, meshName);
                if (warnedTexCoordParts?.Add(part) == true)
                    foreach (string warning in MeshGltf.TexCoordTransportWarnings(mesh, part))
                        log?.Report(warning);
                var partTex = PartTextureResolver.Resolve(scope, reader, RequiredDec, outfit, part,
                    mesh.Submeshes.Count);
                // the renderer bound no textures — the preview rebuilds untextured, but never silently
                if (partTex.All.Count == 0)
                    log?.Report($"{part} opens untextured: its materials bind no maps.");
                // the part's maps land in texDir here — this is what makes the resolve below find anything
                EnsureWorkspacePngs(partTex);
                // resolve each renderer texture to its bundle-scoped workspace PNG; a map that isn't there is
                // named to the caller, never silently dropped
                var (baseColorPng, normalPng, perSubmesh) =
                    ResolvePartPngs(texDir, subjectSlug, partTex, missedTextures);
                var stockIndexCounts = mesh.Submeshes.Select(indices => indices.Length).ToList();
                var partAuthored = authoredMaps is null ? null : authoredMaps.GetValueOrDefault(part);
                var partAuthoredTextures = authoredTextureMaps is null
                    ? null : authoredTextureMaps.GetValueOrDefault(part);
                // The modder's OWN maps take their material positions from the stock ones, exactly as the
                // lone route's re-export does, so an open-all session shows the work they painted rather than
                // the game texture under it.
                perSubmesh = OverlayAuthoredMaps(perSubmesh, partAuthored);
                // The outbound inventory, projected over the primitives the session OPENS: the stock mesh's,
                // or an edited part's own, whose extra submeshes fold onto the last drawable material exactly
                // as the build draws them and the edit's cards slot them. The modder's pictures ride the same
                // rows, each on the primitive it was authored for.
                IReadOnlyList<TextureTransportSource> Transport(int primitiveCount, bool reportMissed)
                {
                    var rows = ResolveTextureTransport(texDir, subjectSlug, meshName, partTex, workspaceSrgb,
                        reportMissed ? missedTextures : null, primitiveCount, stockIndexCounts);
                    if (partAuthored is not null)
                    {
                        var fixedOverrides = partAuthored.SelectMany((maps, submesh) => new[]
                        {
                            maps.Base is null ? default(TextureTransportOverride?)
                                : new TextureTransportOverride(submesh, "", maps.Base, MapKind.BaseColor, submesh),
                            maps.Normal is null ? default(TextureTransportOverride?)
                                : new TextureTransportOverride(submesh, "", maps.Normal, MapKind.Normal, submesh),
                            maps.Rmo is null ? default(TextureTransportOverride?)
                                : new TextureTransportOverride(submesh, "", maps.Rmo, MapKind.Rmo, submesh),
                        }).OfType<TextureTransportOverride>().ToList();
                        rows = OverlayAuthoredTextures(rows, fixedOverrides, markAuthored: false);
                    }
                    return OverlayAuthoredTextures(rows, partAuthoredTextures, markAuthored: false);
                }
                var textureTransport = Transport(mesh.Submeshes.Count, reportMissed: true);
                if (skin is { IsSkinned: true })
                {
                    var uprighting = partRest;
                    // The modder's own geometry wins for an edited part: its workspace glb holds the authored
                    // mesh AND skin, so the session opens on what they last sent rather than the game copy
                    // their next send would overwrite. It already sits in the space the Add put it in, so it
                    // combines with no further uprighting. Maps come from the workspace PNGs and the part's
                    // own authored files — a workspace glb carries neither.
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
                                PerSubmesh: perSubmesh,
                                TextureTransport: e.Mesh.Submeshes.Count == mesh.Submeshes.Count
                                    ? textureTransport
                                    : Transport(e.Mesh.Submeshes.Count, reportMissed: false)));
                            riggedSlots.Add(meshName);
                            done.Add(part);
                            continue;
                        }
                        // never silent: the game copy opens instead, and the caller says which part
                        vanillaFallbacks?.Add(part);
                        log?.Report($"Couldn't read {part}'s edit. The original opens instead.");
                    }
                    if (sceneRig is null && bones.Count == 0) anyHashNamedRig = true;
                    // null glbOut ⇒ collect for the combined glb only, don't rewrite the per-part glb. The
                    // lone glb is a REPLACEABLE part's round-trip file, so it takes no prefab placement:
                    // mesh and joints land together at the part's own origin, as the combined session puts a
                    // replaceable part (see CombinedPose).
                    if (glbOut is not null)
                        pendingLone.Add(new PendingLoneGlb(part, meshName, glbOut, mesh, skin, baseColorPng,
                            normalPng, perSubmesh, sceneRig?.BonePaths, uprighting, sceneRig?.ConnectorRests,
                            textureTransport));
                    var (contextPose, connectors) = CombinedPose(sceneRig, uprighting,
                        // the gated read is the expensive half, so it runs only where the answer can matter
                        () => Workbench.PartSkinGate.Blocked(RequiredDec, srcBundle, meshName, pathId, reader) is null);
                    rigged.Add(new MeshGltf.RiggedPart(mesh, skin, baseColorPng, normalPng,
                        sceneRig?.BonePaths, uprighting, connectors,
                        PerSubmesh: perSubmesh,
                        ContextPose: contextPose,
                        TextureTransport: textureTransport));   // for the union combined
                    riggedSlots.Add(meshName);
                    done.Add(part);
                }
                else if (glbOut is not null)   // rigid prop: no rig, but still upgrade its bare Add glb to textured
                {
                    MeshGltf.ExportGlb(mesh, glbOut, baseColorPng, normalPng, perSubmesh, partRest,
                        onUnreadableMap: MapWouldNotDecode, textureTransport: textureTransport);
                    done.Add(part);
                }
            }
            // A game-locked read is a WHOLE-run BUSY condition, not a per-part failure: game-file-locked must
            // never degrade into empty output. Genuine per-part decode faults stay isolated below.
            catch (IOException) { throw; }
            catch
            {
                diagnostics?.TransientFailure();
                /* skip this part, keep building the others */
            }
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
            log?.Report("Some bones open under generated names.");

        var skeleton = SubjectSkeleton(unionParts, bones.Path, out var disagreeing);
        foreach (var line in DisagreementLines(disagreeing)) log?.Report(line);

        // The subject's candidacy roster, measured the way the BUILD measures it (ModBuilder's RosterProbe):
        // bone table + narrow layout + presence + posed bones + the prefab's shadow and visibility flags. It
        // is what decides which of the subject's bones a tail may offer, and it is read for the whole subject
        // — the rows above cover only the parts this project materialized.
        var candidacy = CandidacyRoster(roster, reader, logical => Dec(logical, required: false),
            measuredInLoop, cache, CandidacyKey,
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
                    m => log?.Report($"{w.Part} · {m}"), MapWouldNotDecode, w.TextureTransport, previewMemo);
            }
            catch (IOException) { throw; }
            catch (Exception e)
            {
                diagnostics?.TransientFailure();
                // the part's own glb is what a lone session opens and what its compile round trips, so a
                // write that didn't land must not be reported as rigged
                done.Remove(w.Part);
                log?.Report($"Couldn't build the rig for {w.Part}: {e.Message}");
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
                CombinedExtraBones(skeleton, rigged, combinedValid), m => log?.Report(m),
                MapWouldNotDecode, previewMemo);
        }
        foreach (var name in missedTextures) unreadableTextures?.Add(name);
        if (parts.Any(part => part.GlbOut is not null
            && (!done.Contains(part.Part) || !File.Exists(part.GlbOut))))
            diagnostics?.TransientFailure();
        return done;
    }

    /// <summary>The per-submesh map set with the modder's own files laid over the stock ones, slot by slot: an
    /// authored base colour replaces the stock base colour and leaves the normal and RMO where they are.
    /// <paramref name="authored"/> is indexed the way the lone route's re-export indexes it — entry <i>i</i>
    /// answers submesh <i>i</i>, and entries past the submesh count are ignored — so the two routes put the
    /// same file in the same place. Returns <paramref name="stock"/> itself when there is nothing to lay
    /// over.</summary>
    private static List<(string?, string?, string?)> OverlayAuthoredMaps(
        List<(string?, string?, string?)> stock,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authored)
    {
        if (authored is null || authored.Count == 0) return stock;
        for (int i = 0; i < stock.Count && i < authored.Count; i++)
            stock[i] = (authored[i].Base ?? stock[i].Item1,
                        authored[i].Normal ?? stock[i].Item2,
                        authored[i].Rmo ?? stock[i].Item3);
        return stock;
    }

    /// <summary>Lay project pictures over exact property rows without changing their stock resource identity.
    /// Exact property wins; a property-less legacy fixed override may match only its coarse semantic.</summary>
    private static IReadOnlyList<TextureTransportSource> OverlayAuthoredTextures(
        IReadOnlyList<TextureTransportSource> stock, IReadOnlyList<TextureTransportOverride>? authored,
        bool markAuthored = true)
    {
        if (authored is null || authored.Count == 0) return stock;
        return stock.Select(binding =>
        {
            var replacement = authored.FirstOrDefault(candidate =>
                candidate.Covers(binding.MaterialIndex, binding.PrimitiveIndex)
                && string.Equals(candidate.ShaderProperty, binding.ShaderProperty, StringComparison.Ordinal));
            if (replacement.Png is not { Length: > 0 })
                replacement = authored.FirstOrDefault(candidate =>
                    candidate.Covers(binding.MaterialIndex, binding.PrimitiveIndex)
                    && candidate.ShaderProperty.Length == 0 && candidate.Kind == binding.Kind);
            return replacement.Png is { Length: > 0 } png
                ? binding with
                {
                    Png = png,
                    Origin = markAuthored ? MapOrigin.Authored : MapOrigin.Vanilla,
                    Label = replacement.Label ?? binding.Label,
                }
                : binding;
        }).ToList();
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
        IReadOnlyDictionary<string, Matrix4x4>? ConnectorRests,
        IReadOnlyList<TextureTransportSource>? TextureTransport);

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
            yield return $"Bone {disagreeing[i]} is off the armature: this item's parts bind it in "
                         + "different places.";
        if (disagreeing.Count > NamedDisagreements)
            yield return $"…and {disagreeing.Count - NamedDisagreements} more bones bind in different "
                         + "places, all off the armature.";
    }

    /// <summary>
    /// The whole subject's skeleton in the order its parts were read: every bone any part skins, named the
    /// way that part's export names it (its scene rig first, then the bone table, then a flat
    /// <c>bone_&lt;hash8&gt;</c>) so one bone reaches one armature node however many parts pose it.
    ///
    /// <para>Bind poses are the placement source, and the first part to name a bone fixes both its path and
    /// its rest — but only while the subject AGREES about it. Agreement is judged in SCENE space, each part's
    /// bind rest composed with its own uprighting: a subject whose body ships lying down while its hair
    /// ships upright binds the head in two bind spaces and one scene place, and that is one bone. A bone
    /// two parts place differently in the scene has no one rest to stand at, so it is dropped from the
    /// skeleton entirely and named in <paramref name="disagreeing"/>: it still poses the parts that own
    /// it, it just never joins another part's armature. An armature stick in the wrong place is worse
    /// than an absent one.</para>
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
                    var placed = uprighting is { } g ? rest * g : rest;
                    var held = bones[at].Uprighting is { } hg ? bones[at].BindRest * hg : bones[at].BindRest;
                    if (RestBake.RotationDiff(placed, held) <= RestBake.RotationTol
                        && RestBake.TranslationDiff(placed, held) <= PlacementAgreementTol)
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
            catch (Exception e) { log?.Report($"uv    {part}: guide for {Path.GetFileName(png)} failed: {e.Message}"); }
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
        IReadOnlyList<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> samplers, string guidePath,
        string channel = "TexCoord0", (int Width, int Height)? canvasSize = null)
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
        if (canvasSize is { Width: > 0, Height: > 0 } size)
        { gw = size.Width; gh = size.Height; }
        else
        {
            var texDec = Dec(textureBundleId);
            if (texDec is not null && reader.GetTextureMeta(texDec, textureName) is { } meta
                && meta.Width > 0 && meta.Height > 0)
            { gw = meta.Width; gh = meta.Height; }
        }

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

        return PlotUvGuide(samplers, gw, gh, textureName, guidePath, ResolveVanilla, channel);
    }

    /// <summary>The guide-rendering core, split out so the modded-vs-vanilla mesh choice is testable without
    /// a <see cref="GameVfs"/>. For each distinct mesh among <paramref name="samplers"/> it loads the
    /// geometry — PREFERRING the part's edited workspace glb (<c>ModdedGlb</c>) for every UV channel it
    /// carries, and using <paramref name="resolveVanilla"/> for an unedited part or a legacy edit that lacks
    /// the requested channel — then merge-plots
    /// its sampling submeshes onto <paramref name="guidePath"/> at
    /// <paramref name="gw"/>×<paramref name="gh"/>. The guide is rebuilt FRESH (prior file deleted) so an
    /// edit's new UV layout replaces the stale one instead of overlaying it. A submesh index past a
    /// merged-down edited mesh's submesh count is skipped, never a crash. Returns null on success (≥1 mesh
    /// plotted), else the user-facing reason.</summary>
    internal static string? PlotUvGuide(
        IReadOnlyList<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> samplers,
        int gw, int gh, string textureName, string guidePath,
        Func<string, string, UnityMesh?> resolveVanilla, string channel = "TexCoord0")
    {
        try { if (File.Exists(guidePath)) File.Delete(guidePath); } catch { /* rebuild overwrites/merges */ }

        int plotted = 0;
        int readable = 0;
        bool missingChannel = false;

        string Refuse(string message)
        {
            try { if (File.Exists(guidePath)) File.Delete(guidePath); } catch { }
            return message;
        }

        foreach (var group in samplers.GroupBy(s => s.MeshName, StringComparer.Ordinal))
        {
            UnityMesh? mesh = null;
            var moddedGlb = group.Select(s => s.ModdedGlb).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            if (moddedGlb is not null)
            {
                try { mesh = MeshGltf.ImportGlb(moddedGlb, null); }
                catch
                {
                    if (channel == "TexCoord0")
                        return Refuse("Couldn't read this edit's mesh, so no UV guide was drawn. "
                            + "Send it back from Blender again, or use Revert mesh.");
                    mesh = null;
                }
                if (channel == "TexCoord0" && mesh?.Has(channel) != true)
                    return Refuse("This edit's mesh has no UV layout, so no UV guide can be drawn.");
            }
            // A legacy edit may predate higher-UV transport. It falls back to the game layout; a current
            // edit carries the channel and therefore stays authoritative for its own guide.
            if (mesh?.Has(channel) != true)
                mesh = resolveVanilla(group.Key, group.Select(s => s.MeshAddress).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? "");
            if (mesh is null) continue;
            readable++;
            if (!mesh.Has(channel)) { missingChannel = true; continue; }
            if (UvGuide.TryRenderMerge(mesh, group.Select(s => s.Submesh).ToList(), gw, gh, guidePath, channel))
                plotted++;
        }
        if (plotted > 0) return null;
        if (channel == "TexCoord1" && missingChannel)
            return readable == 1
                ? $"The mesh that samples {textureName} has no second UV set, so no UV1 guide can be drawn."
                : $"The meshes that sample {textureName} have no second UV set, so no UV1 guide can be drawn.";
        if (channel == "TexCoord0" && missingChannel)
            return readable == 1
                ? $"The mesh that samples {textureName} has no UV layout, so no UV guide can be drawn."
                : $"The meshes that sample {textureName} have no UV layout, so no UV guide can be drawn.";
        return $"Couldn't read the meshes that sample {textureName} from the game. Rescan, then try again.";
    }

    /// <summary>Resolve a part's renderer-bound textures to their on-disk workspace PNGs. The file is named
    /// by the renderer PPtr's own pinned bundle through <see cref="TextureExport.BundleScopedName"/> — the
    /// single naming rule every producer of that folder shares, and the one the open's own texture pass
    /// writes under. There is no name-convention re-derivation: resolving name-only would miss every file and
    /// strip the rebuilt glb of its maps. A path that isn't on disk names its texture in
    /// <paramref name="missed"/>, never silently. Returns the part-level base/normal PNGs and the per-submesh
    /// (base, normal, RMO) set — the maps the Blender-facing glb embeds.</summary>
    internal static (string? BaseColor, string? Normal, List<(string?, string?, string?)> PerSubmesh) ResolvePartPngs(
        string texDir, string subjectSlug, PartTextures partTex, ICollection<string>? missed = null)
    {
        // one resolution per exact resource; the name-only fallback remains for old SubmeshMaps rows.
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in partTex.All)
        {
            // Only the maps the glb embeds are resolved: base colour, normal and RMO. A material's other
            // maps — the toon ramp above all — never exist as workspace PNGs, so resolving them here
            // reported a "missing" file per material on every open for maps the export never ships.
            if (!(t.IsBaseColor || t.IsNormal || t.IsRmo)) continue;
            // a null bundle folds to the deterministic "_" segment (as the producers do); that file was never
            // written, so File.Exists is false and the miss is named below
            var file = Path.Combine(texDir, WorkspaceTextureName(partTex, t, subjectSlug));
            if (File.Exists(file)) resolved[ResourceKey(t)] = file;
            else
            {
                resolved[ResourceKey(t)] = null;
                missed?.Add(t.Name);
            }
        }
        string? Png(TexTarget? target, string? name)
        {
            var selected = target ?? partTex.All.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
            return selected.Name is not null && resolved.TryGetValue(ResourceKey(selected), out var file)
                ? file : null;
        }
        var perSubmesh = partTex.Submeshes
            .Select(sm => (Png(sm.BaseColorTarget, sm.BaseColor), Png(sm.NormalTarget, sm.Normal),
                Png(sm.RmoTarget, sm.Rmo)))
            .ToList<(string?, string?, string?)>();
        var firstBase = partTex.All.FirstOrDefault(t => t.IsBaseColor);
        var firstNormal = partTex.All.FirstOrDefault(t => t.IsNormal);
        return (Png(firstBase.Name is null ? null : firstBase, firstBase.Name),
                Png(firstNormal.Name is null ? null : firstNormal, firstNormal.Name), perSubmesh);
    }

    /// <summary>Resolve the full installed material inventory to property-keyed transport rows. Primitive
    /// projection follows the renderer rule without truncating the inventory: a short material list repeats
    /// its last position across remaining primitives, while surplus positions carry a null primitive.</summary>
    /// <param name="primitiveCount">The primitives the session opens — an edited part's own submesh count,
    /// which may exceed the stock material list; null projects over the stock mesh's submeshes.</param>
    /// <param name="stockIndexCounts">The stock mesh's index count per submesh, which says which material
    /// positions draw at all; null treats every stock position as drawable.</param>
    internal static IReadOnlyList<TextureTransportSource> ResolveTextureTransport(string texDir,
        string subjectSlug, string meshName, PartTextures partTex,
        IReadOnlyDictionary<string, bool?>? srgb = null, ICollection<string>? missed = null,
        int? primitiveCount = null, IReadOnlyList<int>? stockIndexCounts = null)
    {
        var materials = partTex.Materials ?? Array.Empty<MaterialTextureBindings>();
        if (materials.Count == 0) return Array.Empty<TextureTransportSource>();
        int stockSubmeshCount = partTex.Submeshes.Count;
        int materialCount = materials.Max(material => material.MaterialIndex) + 1;
        // A material position fires only where the stock mesh gives it a submesh with indices; past the
        // stock submesh table it is surplus inventory. Unknown counts keep the older answer: every stock
        // position draws.
        bool Drawable(int position) => position < stockSubmeshCount
            && (stockIndexCounts is null || position >= stockIndexCounts.Count || stockIndexCounts[position] > 0);
        // Every primitive the session opens lands on one material position by the shared fold (see
        // MaterialFold): its own below the material count, the last drawable one past it.
        var primitivesByMaterial = new Dictionary<int, List<int?>>();
        for (int primitive = 0; primitive < (primitiveCount ?? stockSubmeshCount); primitive++)
        {
            int position = MaterialFold.MaterialPosition(primitive, materialCount, Drawable);
            if (position < 0) continue;
            if (!primitivesByMaterial.TryGetValue(position, out var owned))
                primitivesByMaterial[position] = owned = new List<int?>();
            owned.Add(primitive);
        }
        var result = new List<TextureTransportSource>();
        foreach (var material in materials)
        {
            if (!primitivesByMaterial.TryGetValue(material.MaterialIndex, out var primitives))
                primitives = new List<int?> { null };
            bool drawable = Drawable(material.MaterialIndex);

            foreach (var binding in material.Textures)
            {
                var texture = binding.Texture;
                if (texture.IsRamp || texture.Bundle is null) continue;
                string png = Path.Combine(texDir,
                    WorkspaceTextureName(partTex, texture, subjectSlug));
                if (!File.Exists(png)) { missed?.Add(texture.Name); continue; }
                var input = texture.IsBaseColor ? TargetInputKind.BaseColor
                    : texture.IsNormal ? TargetInputKind.Normal
                    : texture.IsRmo ? TargetInputKind.Rmo
                    : texture.IsBlend ? TargetInputKind.Blend
                    : TargetInputKind.Texture;
                var kind = UvGuide.MapKindFor(input);
                bool? colorSpace = srgb is not null && srgb.TryGetValue(png, out var known) ? known : null;
                foreach (var primitive in primitives)
                    result.Add(new TextureTransportSource(meshName, material.MaterialIndex, primitive,
                        binding.ShaderProperty, kind, png, texture.Name, texture.Bundle, texture.PathId,
                        colorSpace, TexCoord: UvGuide.TexCoordIndex(input), Drawable: drawable));
            }
        }
        return result;
    }

    private static string ResourceKey(TexTarget texture) =>
        $"{texture.Bundle}\u001f{texture.PathId}\u001f{(texture.PathId == 0 ? texture.Name : "")}";

    /// <summary>The stable workspace name, extended with path id only when this inventory contains another
    /// exact resource with the same bundle and name.</summary>
    private static string WorkspaceTextureName(PartTextures part, TexTarget texture, string subject) =>
        part.All.Any(other => other.PathId != texture.PathId
            && string.Equals(other.Bundle, texture.Bundle, StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.Name, texture.Name, StringComparison.Ordinal))
            ? TextureExport.BundleScopedName(texture.Bundle ?? "", texture.Name, subject, texture.PathId)
            : TextureExport.BundleScopedName(texture.Bundle ?? "", texture.Name, subject);

    private static string Safe(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_'));
}
