using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Workbench;

namespace Remold.Core.Migoto;

/// <summary>What one subject's roster looks like to a pool derivation: the bone sets it can pool over, the
/// parts it could not read and why, each part's measured scene rest, and the subject's own skeleton by bone
/// hash. One read of each mesh answers all five, which is why they travel together.</summary>
public sealed record RosterProbeResult(
    List<PoolDerive.PartBones> Bones,
    Dictionary<string, SubjectPart> BySlot,
    List<PoolDerive.MissingPart> HeldBack,
    Dictionary<string, Matrix4x4?> Rests,
    Dictionary<uint, string> BonePaths);

/// <summary>
/// Reads a subject the way a Replace's pool derivation reads it, and answers the one question a caller
/// outside a build needs from that: which part a replacement's donor actually draws at.
///
/// <para><b>Why it is its own unit.</b> The build derives the pool to compile against it; the ramp
/// conversion needs only the ANCHOR, because that is the part whose own toon ramp a replaced submesh would
/// otherwise shade with (see <see cref="ModBuilder"/>'s donor maps: the pool's anchor on the pooled route,
/// the replaced part itself on the rigid one). Two derivations of that answer could disagree, and the one
/// that disagreed would be the one nobody was watching — so both go through here.</para>
///
/// <para><b>What it costs.</b> A probe reads EVERY part of the subject out of its bundle, and an anchor
/// additionally imports the replacement's donor glb. Both are memoised — per subject, and per (subject,
/// part, donor file) — and both are done lazily, on the first call that needs them. A caller that asks
/// about no target pays nothing; a project whose ramp slots are all settled asks about no target.</para>
/// </summary>
public sealed class SubjectPoolProbe
{
    private readonly BuildEnv _env;
    private readonly Bundles.BundleReader _reader;
    private readonly ICollection<string>? _diagnostics;
    private readonly Dictionary<string, byte[]?> _bundles = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), RosterProbeResult> _probes = new();
    private readonly Dictionary<string, IReadOnlyList<Tables.PartScheme.Slot>?> _schemes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Bundles.TimelineShoe>?> _shoes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>?> _schemeTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubjectPart?> _anchors = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="reader">the bundle reader to parse through. Pass a build's own to share its parses;
    /// omit for one of this probe's own.</param>
    /// <param name="diagnostics">receives the per-part exclusions a probe records. Null discards them,
    /// which is what a caller with no build log to write to passes.</param>
    public SubjectPoolProbe(BuildEnv env, Bundles.BundleReader? reader = null,
        ICollection<string>? diagnostics = null)
    {
        _env = env;
        _reader = reader ?? new Bundles.BundleReader();
        _diagnostics = diagnostics;
    }

    /// <summary>A logical bundle's deobfuscated bytes, parsed once. <paramref name="why"/> names what asked,
    /// for the refusal an install that cannot serve it raises.</summary>
    public byte[] Bundle(string id, string why)
    {
        if (!_bundles.TryGetValue(id, out var bytes))
            _bundles[id] = bytes = _env.Deobfuscate(id);
        if (bytes is null) _diagnostics?.Add($"bundle '{id}' ({why}) isn't readable in this install");
        return bytes ?? throw new AuthoredRefusalException(
            $"the game files for {why} can't be read in this install");
    }

    /// <summary>Every tier of a part, forward-resolved: (mesh name, bundle id, path id).</summary>
    public List<(string Name, string BundleId, long PathId)> Tiers(SubjectPart part)
    {
        var list = new List<(string, string, long)>
        {
            ResolveTier(part.SlotName, part.MeshAddress, part.MeshBundle, part.MeshPathId),
        };
        foreach (var t in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
            list.Add(ResolveTier(t.SlotName, t.MeshAddress, t.MeshBundle, t.MeshPathId));
        return list;

        (string, string, long) ResolveTier(string name, string address, string? smrBundle, long smrPathId)
        {
            RefuseBlocked(name, address);
            if (!string.IsNullOrEmpty(smrBundle) && smrPathId != 0) return (name, smrBundle!, smrPathId);
            if (string.IsNullOrEmpty(address))
                throw new InvalidOperationException(
                    $"mesh '{name}' carries no recipe address and no resolved renderer mesh");
            var owner = _env.ResolveAddress(address)
                ?? throw new InvalidOperationException(
                    $"no catalog entry for mesh address '{address}' (mesh '{name}')");
            return (name, owner, 0);
        }
    }

    /// <summary>The outfit's wardrobe scheme, read once per outfit: null = it is not modular, or no resolver
    /// knows it. THE one place the scheme is asked for, so the roster probe's presence and every wardrobe
    /// question read the same answer. Keyed on the stem exactly as it was given: the resolver decides what
    /// two spellings of one name mean, and a cache that folded case would answer for a stem the resolver was
    /// never asked about.</summary>
    public IReadOnlyList<Tables.PartScheme.Slot>? SchemeOf(SubjectModel model) =>
        _schemes.TryGetValue(model.Stem, out var have)
            ? have : _schemes[model.Stem] = _env.PartSchemeFor?.Invoke(model.Stem);

    /// <summary>The outfit's timeline node overrides, read once per outfit like the scheme. Null = nothing
    /// measured them, which demotes nothing.</summary>
    private IReadOnlyList<Bundles.TimelineShoe>? ShoesOf(SubjectModel model) =>
        _shoes.TryGetValue(model.Stem, out var have)
            ? have : _shoes[model.Stem] = _env.TimelineShoesFor?.Invoke(model.Stem);

    /// <summary>The outfit's modular resource tokens, which is what a timeline entry aimed at the wardrobe
    /// selector matches against. Derived from the same scheme the presence rules read.</summary>
    private IReadOnlySet<string>? ResourceTokensOf(SubjectModel model)
    {
        if (_schemeTokens.TryGetValue(model.Stem, out var have)) return have;
        IReadOnlySet<string>? tokens = null;
        if (SchemeOf(model) is { } slots)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in slots)
                foreach (var variant in slot.Variants)
                    foreach (var token in variant.Tokens) set.Add(token);
            tokens = set;
        }
        return _schemeTokens[model.Stem] = tokens;
    }

    /// <summary>The mechanism, if any, that lets the game withhold this part. The part's own prefab-resident
    /// marker answers first; only when nothing on the prefab named it do the timelines get asked, since
    /// those are a build-time input the workbench model never carries. Every tier name is offered, because a
    /// timeline naming any one of a part's draws makes the whole part unsafe to lean on.</summary>
    public Model.VisibilityOverride VisibilityOf(SubjectModel model, SubjectPart part)
    {
        if (part.Visibility != Model.VisibilityOverride.None) return part.Visibility;
        if (ShoesOf(model) is not { } shoes || shoes.Count == 0) return Model.VisibilityOverride.None;
        var tokens = ResourceTokensOf(model);
        foreach (var shoe in shoes)
            foreach (var entries in new[] { shoe.ShowNodes, shoe.HideNodes })
            {
                if (Model.ShoeNodeMatch.MatchesAny(entries, part.SlotName, part.Token, tokens, model.Stem))
                    return Model.VisibilityOverride.TimelineNamed;
                foreach (var t in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
                    if (Model.ShoeNodeMatch.MatchesAny(entries, t.SlotName, part.Token, tokens, model.Stem))
                        return Model.VisibilityOverride.TimelineNamed;
            }
        return Model.VisibilityOverride.None;
    }

    /// <summary>The roster bone sets a pool derivation reads. A property of the SUBJECT: two Replaces on one
    /// subject probe the same parts, and an unreadable part is one exclusion, not one per Replace.</summary>
    public RosterProbeResult Probe(SubjectModel model)
    {
        var subjectKey = (model.Character.ToLowerInvariant(), model.Stem.ToLowerInvariant());
        if (_probes.TryGetValue(subjectKey, out var have)) return have;

        // The outfit's wardrobe scheme decides part presence. No resolver, or an outfit it doesn't know,
        // classifies by context tail alone — variant-shaped tokens then read unknown, which admits them as
        // targets and keeps them out of every other pool.
        var schemeSlots = SchemeOf(model);

        // Readable parts only. Every part left out is kept with its reason, because a pool derived over a
        // SHORT roster fails in ways only the exclusions explain — and a Replace whose own target is missing
        // here has no pool question to ask at all.
        var bones = new List<PoolDerive.PartBones>();
        var bySlot = new Dictionary<string, SubjectPart>(StringComparer.OrdinalIgnoreCase);
        var heldBack = new List<PoolDerive.MissingPart>();
        var rests = new Dictionary<string, Matrix4x4?>(StringComparer.OrdinalIgnoreCase);
        // One skeleton per subject: hash → '/'-joined full path, from the subject's OWN skeleton — per-part
        // scene rigs only name a part's skin bones and can fail to read at all, while the prefab skeleton
        // holds every chain. A mesh-stored bone hash can name any chain SUFFIX, not just the leaf (SceneRig's
        // own matching rule), so every suffix spelling keys the full path; a hash two different bones can
        // spell is dropped whole — a tie routed to the wrong limb would articulate the wrong side, and the
        // seed is the tamer failure. Feeds the emitter's tie underlay; an unreadable skeleton just leaves it
        // empty (ties degrade by name there).
        var bonePaths = new Dictionary<uint, string>();
        if (model.Skeleton is { } skel)
        {
            var ambiguous = new HashSet<uint>();
            for (int i = 0; i < skel.BoneCount; i++)
            {
                var segs = new List<string>();
                var seen = new HashSet<int>();
                for (int cur = i; cur >= 0 && cur < skel.BoneCount && seen.Add(cur);
                     cur = skel.Bones[cur].ParentIndex)
                    segs.Add(skel.Bones[cur].Name);
                segs.Reverse();
                string path = string.Join("/", segs);
                for (int k = 0; k < segs.Count; k++)
                {
                    uint h = Skeleton.BoneTable.Hash(string.Join("/", segs.Skip(k)));
                    if (bonePaths.TryGetValue(h, out var prev))
                    {
                        if (!string.Equals(prev, path, StringComparison.Ordinal)) ambiguous.Add(h);
                    }
                    else bonePaths[h] = path;
                }
            }
            foreach (var h in ambiguous) bonePaths.Remove(h);
        }
        foreach (var p in model.Parts)
        {
            RefuseBlocked(p.SlotName, p.MeshAddress);   // outside the try: the catch takes a diagnostic and carries on
            try
            {
                var (name, bid, pid) = Tiers(p)[0];
                var field = _reader.GetMeshField(Bundle(bid, $"part '{p.Token}'"), name, pid)
                    ?? throw new AuthoredRefusalException(
                $"the game files no longer hold the mesh '{name}'. Rescan, then build again");
                // ahead of the skin rule: the bone table is what tells a later orphan-bone failure whether a
                // refused part could have owned the bones the pool is missing
                var hashes = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToHashSet();
                // the bone table also settles support: a rig this path can't carry is declined like any
                // other unsupported asset
                if (Skeleton.BoneTable.HasUnsupportedRig(hashes))
                    throw new BlockedAssetException($"'{p.SlotName}' is not a supported asset");
                if (StreamDump.UnrecoverableSkinReason(field) is { } why)
                {
                    heldBack.Add(new PoolDerive.MissingPart(p.SlotName, why, hashes,
                        PartPresence.Classify(p.Token, schemeSlots)));
                    _diagnostics?.Add($"part '{p.Token}' excluded from pool derivation: can't feed palette recovery: {why}");
                    continue;
                }
                // Measured here and nowhere else: the bones this part POSES are what decides whether a donor
                // bone has a palette row to recover and whether the part can carry another's tier bones, and
                // two reads of one mesh could disagree.
                // Its own catch, and it holds the part back: the skin rule passes shapes the weight read
                // still can't take, and a part offered without a measured posed set would fall back to its
                // TABLE at the posed gate — the one thing that gate exists to refuse. The table read above
                // stands, so an orphan-bone refusal can still rule this part out by the bones it owns.
                HashSet<uint> posedBones;
                try
                {
                    posedBones = StreamDump.WeightedBoneHashes(field);
                }
                catch (Exception ex) when (ex is not BlockedAssetException)
                {
                    const string unread = "its skin weights can't be read";
                    heldBack.Add(new PoolDerive.MissingPart(p.SlotName, unread, hashes,
                        PartPresence.Classify(p.Token, schemeSlots)));
                    _diagnostics?.Add($"part '{p.Token}' excluded from pool derivation: {unread}: {ex.Message}");
                    continue;
                }
                bones.Add(new PoolDerive.PartBones(p.SlotName, hashes,
                    Narrow: Mesh.SkinLayout.IsNarrow(field),
                    Presence: PartPresence.Classify(p.Token, schemeSlots),
                    PosedBones: posedBones,
                    CastsShadows: p.CastsShadows,
                    Visibility: VisibilityOf(model, p)));
                bySlot[p.SlotName] = p;
                // The part's measured scene rest rides the probe: a delta composed of two measured rests is
                // how the union restates a part sharing too few bones with the anchor for a fitted delta.
                // Best-effort — an unmeasurable rest leaves the fitted path, never holds the part back.
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
                heldBack.Add(new PoolDerive.MissingPart(p.SlotName, ex.Message, BoneHashes: null,
                    PartPresence.Classify(p.Token, schemeSlots)));
                _diagnostics?.Add($"part '{p.Token}' excluded from pool derivation: {ex.Message}");
            }
        }
        return _probes[subjectKey] = new RosterProbeResult(bones, bySlot, heldBack, rests, bonePaths);
    }

    /// <summary>The pool a Replace on <paramref name="part"/> derives, over the probed roster: the candidate
    /// set, what candidacy left out, the coverage group and the derivation itself. THE one place those four
    /// calls are made in order, so a caller outside a build cannot arrive at a different pool than the build
    /// will.</summary>
    public (IReadOnlyList<PoolDerive.PartBones> Candidates, IReadOnlyList<PoolDerive.MissingPart> LeftOut,
        IReadOnlyList<PoolDerive.VariantGroup> Groups, PoolDerive.Result Derived)
        Derive(SubjectModel model, SubjectPart part, MeshApply.Payload donor, string? anchorOverride)
    {
        var probed = Probe(model);
        // The roster this Replace may pool over. A part is in it only when it is on screen whenever the
        // target is (a one-influence part only when it IS the target), and what it is left out of is both
        // halves at once — the derivation here and the tier coverage after it read this one set.
        var (candidates, leftOut) = PoolDerive.PoolCandidates(probed.Bones, part.SlotName,
            model.PartsPoolAlone);
        // The coverage group: the bones with an on-screen poser in every variant×context state the target
        // displays in. A variant or context part is no pool candidate, so this is read off the WHOLE roster;
        // what the candidates already pose is subtracted inside.
        var groups = PoolDerive.VariantGroups(probed.Bones, SchemeOf(model), probed.HeldBack, candidates,
            part.SlotName, model.PartsPoolAlone);
        // The parts held back go WITH the roster: they are what tells an orphan-bone refusal apart from a
        // donor genuinely weighted to another armature.
        var derived = PoolDerive.Derive(donor, candidates, anchorOverride,
            probed.HeldBack.Concat(leftOut).ToList(), part.SlotName, groups);
        return (candidates, leftOut, groups, derived);
    }

    /// <summary>
    /// The part a replacement's donor maps BIND AT — the pool's anchor on the pooled route, and the replaced
    /// part itself on the rigid one, where the donor draws nowhere else. This is the part whose own toon
    /// ramp a replaced submesh shades with when nothing carries another, which is what makes it the anchor
    /// the ramp content gate has to compare against.
    ///
    /// <para>Answers the REPLACED part wherever the pool can't be derived — an install that can't read the
    /// mesh, a donor glb that won't import, a derivation that refuses. Those are all conditions the build
    /// itself will report in its own words; a ramp pass has no business failing a project load over one, and
    /// the replaced part is both the rigid route's real answer and the commonest pooled one. A blocked asset
    /// is the exception, as it is everywhere: the content refusal travels.</para>
    ///
    /// <para>Memoised per (subject, part, donor file). See the type's own note on what a first call
    /// costs.</para>
    /// </summary>
    public SubjectPart? AnchorFor(SubjectModel model, SubjectPart part, string? donorGlb,
        string? anchorOverride = null)
    {
        string key = $"{model.Character}|{model.Stem}|{part.SlotName}|{donorGlb}|{anchorOverride}";
        if (_anchors.TryGetValue(key, out var have)) return have;
        return _anchors[key] = Resolve();

        SubjectPart? Resolve()
        {
            try
            {
                var (name, bid, pid) = Tiers(part)[0];
                var field = _reader.GetMeshField(Bundle(bid, $"part '{part.Token}'"), name, pid);
                // A mesh with no influences at all replaces rigidly, and a rigid donor draws at its own
                // part and nowhere else. A mesh neither route reaches is the build's refusal to make.
                if (field is null || StreamDump.Route(field) != StreamDump.ReplaceRoute.Pooled) return part;
                if (donorGlb is null || !File.Exists(donorGlb)) return part;
                var payload = MeshGltf.ImportPayload(donorGlb, lenient: true);
                var derived = Derive(model, part, payload, anchorOverride).Derived;
                return Probe(model).BySlot.GetValueOrDefault(derived.Anchor) ?? part;
            }
            catch (BlockedAssetException) { throw; }
            catch { return part; }
        }
    }

    /// <summary>Fail on a blocked game asset. Don't weaken or drop the calls to this
    /// (<see cref="BuildBlacklist"/>).</summary>
    private static void RefuseBlocked(params string?[] names)
    {
        foreach (var n in names)
            if (BuildBlacklist.IsBlocked(n))
                throw new BlockedAssetException($"'{n}' is not a supported asset");
    }
}
