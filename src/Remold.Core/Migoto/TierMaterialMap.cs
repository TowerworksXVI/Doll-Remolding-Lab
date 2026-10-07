using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Workbench;

namespace Remold.Core.Migoto;

/// <summary>How one lod0 material position found (or failed to find) its carrier at a tier.</summary>
public enum TierMapRule
{
    /// <summary>The tier binds the SAME material asset at that position.</summary>
    Identity,

    /// <summary>No identity match; the tier submesh holding the region's geometry carries it.</summary>
    Geometry,

    /// <summary>No carrier: the lod0 region sits on geometry the tier already draws under another
    /// position, so drawing the range again would double it.</summary>
    Coincident,

    /// <summary>No carrier: the region is not present at the tier at all.</summary>
    Absent,

    /// <summary>No carrier, and the geometry does not say why. The one rule a build warns about.</summary>
    Unresolved,
}

/// <summary>What the geometry layer says about one unmatched lod0 position: the tier submesh carrying its
/// region, or null with the rule that says why there is none.</summary>
public readonly record struct GeometryVerdict(int? Carrier, TierMapRule Rule);

/// <summary>One lod0 material position's answer at a tier: the tier material position that carries it
/// (null = the tier draws that region nowhere), and how that was decided.</summary>
public readonly record struct TierMapEntry(int Position, int? Carrier, TierMapRule Rule);

/// <summary>
/// Which tier material position carries each lod0 material position's region, for one part at one LOD
/// tier. The game orders every tier's renderer materials independently, so lod0 position k and tier
/// position k are unrelated; a donor range drawn at the tier's position k renders under whatever material
/// that tier happens to bind there.
///
/// <para>Two layers. IDENTITY pairs positions binding the same material asset (bundle + path id), greedily
/// in position order, each tier position used once. GEOMETRY is asked about the lod0 positions identity
/// left over, through a callback the caller supplies — which tier submesh holds that region. A BUILD
/// supplies no answer there: it routes on material identity alone, so every position identity leaves over
/// reads unresolved and its range is not drawn at that tier.</para>
///
/// <para>Positions are DRAW-SHAPE positions on both sides. A renderer whose material list is LONGER than
/// its mesh's submesh list redraws the last submesh once per extra material; those extra draws carry the
/// same draw shape as the last submesh, so the section keyed on that shape already fires for all of them
/// and the extra materials are not positions. A material list SHORTER than the submesh list leaves the
/// trailing positions outside the map, where <see cref="Carrier"/> answers with the position itself.</para>
/// </summary>
public sealed class TierMaterialMap
{
    private readonly Dictionary<int, TierMapEntry> _byPosition;

    private TierMaterialMap(IReadOnlyList<TierMapEntry> entries)
    {
        Entries = entries;
        _byPosition = entries.ToDictionary(e => e.Position);
        Diagnostic = string.Join(", ", entries.Select(e =>
            $"{e.Position}→{(e.Carrier is { } q ? q.ToString() : "none")} {e.Rule.ToString().ToLowerInvariant()}"));
    }

    /// <summary>One entry per mapped lod0 position, in position order.</summary>
    public IReadOnlyList<TierMapEntry> Entries { get; }

    /// <summary>The map as one build-log line: <c>0→2 geometry, 1→1 identity, 2→0 identity</c>.</summary>
    public string Diagnostic { get; }

    /// <summary>The map's answer for lod0 position <paramref name="position"/>: <paramref name="carrier"/>
    /// is the tier material position carrying that region, or null when the tier draws the region nowhere.
    ///
    /// <para>FALSE means the map does not cover the position and says nothing about it — the trailing
    /// positions of a mesh with more submeshes than its renderer binds materials for. The caller keeps
    /// whatever it did before the map existed; it must NOT read a no-answer as a dropped range.</para></summary>
    public bool TryCarrier(int position, out int? carrier)
    {
        bool mapped = _byPosition.TryGetValue(position, out var entry);
        carrier = mapped ? entry.Carrier : null;
        return mapped;
    }

    /// <summary>Every position the geometry could not account for — one build warning each.</summary>
    public IEnumerable<TierMapEntry> UnresolvedPositions =>
        Entries.Where(e => e.Rule == TierMapRule.Unresolved);

    /// <summary>The identity a KEPT map is valid under: the hand-bumped revision of the rules no constant
    /// can show — layer A's pairing, the order layer B asks its questions in, and what each answer means —
    /// plus the VALUE of every threshold layer B is parameterised by, so editing one retires the maps it
    /// would have changed without anyone remembering to bump this. A build keeps no maps; this is for a
    /// caller that does.</summary>
    public static string RulesVersion => $"map2|{TierGeometryProbe.Thresholds}";

    /// <summary>The map these entries state, for a caller that kept a finished map rather than measuring
    /// it again. The entries ARE the map: everything else it answers is derived from them. A build does
    /// not take this route: it measures identity on every build.</summary>
    public static TierMaterialMap FromEntries(IReadOnlyList<TierMapEntry> entries) => new(entries);

    /// <summary>The renderer's ordered materials as bare identities, by the same three-form rule
    /// <see cref="TierMaterialRef"/> states. The lod0 side of the map comes through here, so both sides
    /// are keyed the same way.</summary>
    public static IReadOnlyList<TierMaterialRef> IdentitiesOf(IReadOnlyList<SubjectMaterial> materials) =>
        materials.Select(m => m.IsPlaceholder ? new TierMaterialRef(null, 0, true)
            : m.Bundle is null ? new TierMaterialRef(null, m.PathId, false)
            : new TierMaterialRef(m.Bundle, m.PathId, true)).ToList();

    /// <summary>Build the map. <paramref name="geometry"/> answers layer B for one lod0 position and is
    /// called ONLY for positions layer A left unmatched, so a part whose materials all pair costs no mesh
    /// read at all. A build passes a callback that answers "no carrier, unresolved" to every ask, which
    /// makes its maps layer A's alone.</summary>
    public static TierMaterialMap Build(
        IReadOnlyList<TierMaterialRef> lod0Materials, DrawShapeSet lod0Shapes,
        IReadOnlyList<TierMaterialRef> tierMaterials, DrawShapeSet tierShapes,
        Func<int, GeometryVerdict> geometry)
    {
        // draw-shape positions on each side, and only those the renderer binds a material for
        int lod0Count = Math.Min(lod0Shapes.Shapes.Count, lod0Materials.Count);
        int tierCount = Math.Min(tierShapes.Shapes.Count, tierMaterials.Count);

        // ---- layer A: identity, greedy in position order, each tier position used once ----------------
        var carrier = new int?[lod0Count];
        var rule = new TierMapRule[lod0Count];
        var paired = new bool[lod0Count];
        var taken = new bool[tierCount];
        for (int p = 0; p < lod0Count; p++)
            for (int q = 0; q < tierCount; q++)
            {
                if (taken[q] || !SameMaterial(lod0Materials[p], tierMaterials[q])) continue;
                taken[q] = true;
                paired[p] = true;
                rule[p] = TierMapRule.Identity;
                // The correspondence is known AND it draws nothing: a zero-index-count submesh is a
                // material slot with no geometry, and an empty renderer slot binds no material at all, so
                // the game issues no draw there and neither does the replacement. That is vanilla parity,
                // not a failure to place the range.
                carrier[p] = Drawn(tierShapes, tierMaterials, q) ? q : null;
                break;
            }

        // ---- layer B: geometry, for the leftovers only ------------------------------------------------
        for (int p = 0; p < lod0Count; p++)
        {
            if (paired[p]) continue;
            // a lod0 position with no geometry of its own issues no draw at lod0, so no donor range ever
            // folds onto it and there is no region to find a carrier for
            if (lod0Shapes.Shapes[p].Count == 0) { rule[p] = TierMapRule.Absent; continue; }
            var verdict = geometry(p);
            // a carrier the tier does not draw is no carrier: the same rule layer A pairs under, bounded by
            // the same material-bearing count. A position past that count is a submesh the renderer binds
            // no material for, and one binding an empty slot is a material the game never draws — the game
            // issues no draw for either, so a range sent there would draw nowhere and say nothing about it.
            if (verdict.Carrier is { } q
                && (q < 0 || q >= tierCount || !Drawn(tierShapes, tierMaterials, q)))
                verdict = new GeometryVerdict(null, TierMapRule.Unresolved);
            carrier[p] = verdict.Carrier;
            rule[p] = verdict.Rule;
        }

        var entries = new List<TierMapEntry>(lod0Count);
        for (int p = 0; p < lod0Count; p++) entries.Add(new TierMapEntry(p, carrier[p], rule[p]));
        return new TierMaterialMap(entries);
    }

    /// <summary>The tier issues a draw at position <paramref name="q"/>: it has geometry of its own AND the
    /// renderer binds a material there. Unity draws a submesh once per bound material and never for an
    /// empty slot, so a position failing either half is a draw that does not happen and can carry
    /// nothing.</summary>
    private static bool Drawn(DrawShapeSet tierShapes, IReadOnlyList<TierMaterialRef> tierMaterials, int q) =>
        tierShapes.Shapes[q].Count > 0 && !tierMaterials[q].IsPlaceholder;

    /// <summary>The same material asset: same logical bundle and same path id. A placeholder (0:0) equals
    /// only another placeholder; a reference whose CAB did not resolve equals nothing, including another
    /// unresolved one, because neither says what it is.</summary>
    private static bool SameMaterial(TierMaterialRef left, TierMaterialRef right) =>
        left.Resolved && right.Resolved && left.PathId == right.PathId
        && string.Equals(left.Bundle, right.Bundle, StringComparison.Ordinal);
}

/// <summary>
/// Layer B's measurement: given a part's lod0 mesh and one of its tier meshes in the same bind space,
/// which tier submesh holds the region an unmatched lod0 submesh draws.
///
/// <para>Vertex positions only. A tier vertex is CLAIMED by the region when it is nearer to that region's
/// vertices than to the rest of the lod0 mesh and nearer than 5% of the lod0 bounding-box diagonal;
/// whichever tier submesh holds the bulk of the claims is the carrier. Where nothing claims enough, two
/// further readings say the range must not be drawn: the region duplicates surviving geometry the tier
/// already draws, or the region is not at the tier at all.</para>
///
/// <para>A build does not ask this: its routing is decided by material identity alone, so nothing a build
/// emits depends on anything measured here.</para>
/// </summary>
public sealed class TierGeometryProbe
{
    private const int MaxQueryPoints = 3000;
    private const float ClaimRadius = 0.05f;        // of the lod0 bounding-box diagonal
    private const float CoincidenceLimit = 0.01f;   // of the same
    private const float AbsenceRatio = 3f;          // median D→T over median S→T
    private const float CarrierShare = 0.8f;        // of all claims, for the dominant tier submesh
    private const float CarrierClaimFloor = 0.005f; // of the sampled tier vertices

    /// <summary>Every number this reading is parameterised by, round-trip formatted so two that differ
    /// below print precision still read apart. It keys a kept map, which is only this reading's answer
    /// while these are the numbers it was taken with.</summary>
    internal static string Thresholds => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"q{MaxQueryPoints}|claim{ClaimRadius:r}|coinc{CoincidenceLimit:r}|abs{AbsenceRatio:r}"
        + $"|share{CarrierShare:r}|floor{CarrierClaimFloor:r}");

    private readonly IReadOnlyList<Vector3> _lod0Positions;
    private readonly IReadOnlyList<Vector3> _tierPositions;
    private readonly IReadOnlyList<int[]> _lod0Submeshes;
    private readonly int[][] _lod0SubmeshVertices;
    private readonly int[] _lod0Vertices;
    private readonly int[] _lod0SubmeshCount;
    private readonly Dictionary<int, int> _tierOwner = new();
    private readonly int[] _tierQuery;
    private readonly PointGrid _tierGrid;
    private readonly float _diagonal;
    private readonly float _cell;

    public TierGeometryProbe(UnityMesh lod0, UnityMesh tier)
    {
        _lod0Positions = lod0.AsVector3("Vertex");
        _tierPositions = tier.AsVector3("Vertex");
        _lod0Submeshes = lod0.Submeshes;

        // Each lod0 submesh's own vertices, deduped and sorted ONCE. Every position's query set is a
        // reading of these, so the sort is paid per map rather than per position asked about.
        _lod0SubmeshVertices = _lod0Submeshes.Select(s => s.Distinct().Order().ToArray()).ToArray();
        _lod0Vertices = _lod0SubmeshVertices.SelectMany(s => s).Distinct().Order().ToArray();
        // how many submeshes hold each vertex, which is what says whether a vertex SURVIVES the removal of
        // one position: a vertex two submeshes share is still the other's after the one is taken out
        _lod0SubmeshCount = new int[_lod0Positions.Count];
        foreach (var set in _lod0SubmeshVertices)
            foreach (int i in set)
                if ((uint)i < (uint)_lod0SubmeshCount.Length) _lod0SubmeshCount[i]++;

        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var v in _lod0Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        _diagonal = _lod0Positions.Count > 0 ? Vector3.Distance(lo, hi) : 0f;
        // a degenerate mesh gives no scale to judge any distance against; every position reads unresolved
        _cell = _diagonal > 0 ? _diagonal / 48f : 0f;

        // first submesh owning a tier vertex owns it, matching the order the claims are counted in
        for (int q = 0; q < tier.Submeshes.Count; q++)
            foreach (int i in tier.Submeshes[q]) _tierOwner.TryAdd(i, q);
        var tierVertices = _tierOwner.Keys.Order().ToArray();
        _tierQuery = Subsample(tierVertices, MaxQueryPoints);
        _tierGrid = new PointGrid(Points(_tierPositions, tierVertices), _cell);
    }

    /// <summary>The tier submesh carrying lod0 submesh <paramref name="lod0Position"/>'s region, or the
    /// rule that says the range is not drawn at this tier.</summary>
    public GeometryVerdict Verdict(int lod0Position)
    {
        if (_diagonal <= 0 || lod0Position < 0 || lod0Position >= _lod0Submeshes.Count)
            return new GeometryVerdict(null, TierMapRule.Unresolved);

        var dIndices = _lod0SubmeshVertices[lod0Position];
        if (dIndices.Length == 0) return new GeometryVerdict(null, TierMapRule.Unresolved);
        var sIndices = SurvivingVertices(lod0Position);

        var d = Points(_lod0Positions, dIndices);
        var s = Points(_lod0Positions, sIndices);
        var dGrid = new PointGrid(d, _cell);
        var sGrid = s.Length > 0 ? new PointGrid(s, _cell) : null;

        // claims: tier vertices nearer this region than the rest of the lod0 mesh, by tier submesh
        var claims = new Dictionary<int, int>();
        int claimed = 0;
        foreach (int i in _tierQuery)
        {
            var point = _tierPositions[i];
            float toD = dGrid.Nearest(point);
            float toS = sGrid?.Nearest(point) ?? float.MaxValue;
            if (toD >= toS || toD >= ClaimRadius * _diagonal) continue;
            claimed++;
            int owner = _tierOwner[i];
            claims[owner] = claims.GetValueOrDefault(owner) + 1;
        }
        int? dominant = claims.Count > 0
            ? claims.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key
            : null;

        // Nothing survives to compare against: every other lod0 position is this one's own geometry's
        // absence, the tier draws the whole part under whatever it binds, and the one place the range can
        // go is the tier submesh most of the part's geometry sits in.
        if (s.Length == 0)
            return dominant is { } whole
                ? new GeometryVerdict(whole, TierMapRule.Geometry)
                : new GeometryVerdict(null, TierMapRule.Unresolved);

        float claimShare = _tierQuery.Length > 0 ? (float)claimed / _tierQuery.Length : 0f;
        if (dominant is { } best && claimed > 0
            && (float)claims[best] / claimed >= CarrierShare && claimShare > CarrierClaimFloor)
            return new GeometryVerdict(best, TierMapRule.Geometry);

        var dQuery = Subsample(dIndices, MaxQueryPoints);
        var sQuery = Subsample(sIndices, MaxQueryPoints);
        float coincidence = Median(dQuery.Select(i => sGrid!.Nearest(_lod0Positions[i])).ToList());
        if (coincidence < CoincidenceLimit * _diagonal)
            return new GeometryVerdict(null, TierMapRule.Coincident);

        float dToTier = Median(dQuery.Select(i => _tierGrid.Nearest(_lod0Positions[i])).ToList());
        float sToTier = Median(sQuery.Select(i => _tierGrid.Nearest(_lod0Positions[i])).ToList());
        // sToTier of 0 gives the ratio no scale — every distance is "3 times" it, including 0 — so the
        // ratio itself cannot speak for a tier whose surviving geometry sits exactly on the lod0 vertices
        if (sToTier > 0 && dToTier >= AbsenceRatio * sToTier)
            return new GeometryVerdict(null, TierMapRule.Absent);

        // The tier reproduces the surviving geometry exactly and nothing at it claims this region, which
        // was not lying on the surviving geometry either: the tier kept the rest of the part and has
        // nothing anywhere near where this region draws. That is the region being gone at this tier —
        // absence the ratio has no scale to certify, not a question the geometry left open.
        if (sToTier <= 0 && claimed == 0)
            return new GeometryVerdict(null, TierMapRule.Absent);

        return new GeometryVerdict(null, TierMapRule.Unresolved);
    }

    /// <summary>Every lod0 vertex some OTHER submesh draws, in index order — the geometry that survives
    /// when <paramref name="position"/>'s own region is taken out. A vertex this position shares with
    /// another submesh is one of them.</summary>
    private int[] SurvivingVertices(int position)
    {
        var own = _lod0SubmeshVertices[position];
        var isOwn = new bool[_lod0SubmeshCount.Length];
        foreach (int i in own) if ((uint)i < (uint)isOwn.Length) isOwn[i] = true;
        var rest = new List<int>(_lod0Vertices.Length);
        foreach (int i in _lod0Vertices)
        {
            int others = (uint)i < (uint)isOwn.Length
                ? _lod0SubmeshCount[i] - (isOwn[i] ? 1 : 0)
                : 1;
            if (others > 0) rest.Add(i);
        }
        return rest.ToArray();
    }

    private static Vector3[] Points(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var points = new Vector3[indices.Count];
        for (int k = 0; k < indices.Count; k++) points[k] = positions[indices[k]];
        return points;
    }

    /// <summary>At most <paramref name="max"/> of the values, by a fixed stride, so two runs over one mesh
    /// ask about the same points.</summary>
    private static int[] Subsample(int[] values, int max)
    {
        if (values.Length <= max) return values;
        int step = (values.Length + max - 1) / max;
        return values.Where((_, i) => i % step == 0).ToArray();
    }

    private static float Median(List<float> values)
    {
        if (values.Count == 0) return float.MaxValue;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : 0.5f * (values[mid - 1] + values[mid]);
    }

    /// <summary>Exact nearest-point queries over a uniform cell grid, answered by expanding cell shells. The
    /// walk stops as soon as the best point in hand cannot be beaten by a farther shell, and its last shell
    /// is the one that reaches the farthest cell any point sits in — measured from THIS query's cell, so a
    /// query well outside the cloud still comes back with the true nearest point rather than with nothing.
    /// Each shell is walked as a shell (its six faces), never as a cube with the inside skipped.</summary>
    private sealed class PointGrid
    {
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private readonly Vector3[] _points;
        private readonly float _cell;
        private readonly Vector3 _min;
        private readonly (int X, int Y, int Z) _loCell;
        private readonly (int X, int Y, int Z) _hiCell;

        public PointGrid(Vector3[] points, float cellSize)
        {
            _points = points;
            _cell = cellSize > 0 ? cellSize : 1f;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var p in points) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            _min = points.Length > 0 ? lo : Vector3.Zero;
            for (int i = 0; i < points.Length; i++)
            {
                var c = Cell(points[i]);
                if (!_cells.TryGetValue(c, out var list)) _cells[c] = list = new List<int>();
                list.Add(i);
            }
            _loCell = (int.MaxValue, int.MaxValue, int.MaxValue);
            _hiCell = (int.MinValue, int.MinValue, int.MinValue);
            foreach (var (x, y, z) in _cells.Keys)
            {
                _loCell = (Math.Min(_loCell.X, x), Math.Min(_loCell.Y, y), Math.Min(_loCell.Z, z));
                _hiCell = (Math.Max(_hiCell.X, x), Math.Max(_hiCell.Y, y), Math.Max(_hiCell.Z, z));
            }
        }

        private (int, int, int) Cell(Vector3 p) => (
            (int)MathF.Floor((p.X - _min.X) / _cell),
            (int)MathF.Floor((p.Y - _min.Y) / _cell),
            (int)MathF.Floor((p.Z - _min.Z) / _cell));

        public float Nearest(Vector3 query)
        {
            if (_points.Length == 0) return float.MaxValue;
            var (cx, cy, cz) = Cell(query);
            float bestSq = float.MaxValue;
            bool found = false;
            // The walk spans the shells that can hold a point at all: from the first one that reaches the
            // occupied cells to the one that passes the farthest of them. A query sitting well outside the
            // cloud therefore starts where the cloud is instead of stepping through the empty space in
            // between, and still answers with the true nearest point.
            int firstRing = Math.Max(Gap(cx, _loCell.X, _hiCell.X),
                Math.Max(Gap(cy, _loCell.Y, _hiCell.Y), Gap(cz, _loCell.Z, _hiCell.Z)));
            int lastRing = Math.Max(Reach(cx, _loCell.X, _hiCell.X),
                Math.Max(Reach(cy, _loCell.Y, _hiCell.Y), Reach(cz, _loCell.Z, _hiCell.Z)));
            for (int r = firstRing; r <= lastRing; r++)
            {
                void Probe(int dx, int dy, int dz)
                {
                    if (!_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) return;
                    foreach (int i in list)
                    {
                        float distance = Vector3.DistanceSquared(query, _points[i]);
                        if (distance >= bestSq) continue;
                        bestSq = distance;
                        found = true;
                    }
                }
                // the shell's six faces, each clipped to the cells points actually sit in: the shell of a
                // far query is mostly empty space, and walking its full area would cost the ring's size
                // rather than the cloud's
                int xFrom = Math.Max(-r, _loCell.X - cx), xTo = Math.Min(r, _hiCell.X - cx);
                int yFrom = Math.Max(-r, _loCell.Y - cy), yTo = Math.Min(r, _hiCell.Y - cy);
                int zFrom = Math.Max(-r, _loCell.Z - cz), zTo = Math.Min(r, _hiCell.Z - cz);
                for (int dx = xFrom; dx <= xTo; dx++)
                    for (int dy = yFrom; dy <= yTo; dy++)
                        if (dx == -r || dx == r || dy == -r || dy == r)
                            for (int dz = zFrom; dz <= zTo; dz++) Probe(dx, dy, dz);
                        else
                        {
                            if (-r >= zFrom && -r <= zTo) Probe(dx, dy, -r);
                            if (r != -r && r >= zFrom && r <= zTo) Probe(dx, dy, r);
                        }
                // every point in a farther shell is at least r cells away, so the best so far stands
                if (found && bestSq <= r * _cell * (r * _cell)) break;
            }
            return found ? MathF.Sqrt(bestSq) : float.MaxValue;
        }

        /// <summary>Shells from the query's cell <paramref name="c"/> out to the farthest occupied cell on
        /// one axis — how far the walk has to be allowed to go before it can say there is nothing.</summary>
        private static int Reach(int c, int lo, int hi) =>
            Math.Max(Math.Max(c - lo, hi - c), 0);

        /// <summary>Shells from the query's cell <paramref name="c"/> to the NEAREST occupied cell on one
        /// axis; zero while the query sits within the occupied span.</summary>
        private static int Gap(int c, int lo, int hi) =>
            Math.Max(Math.Max(lo - c, c - hi), 0);
    }
}
