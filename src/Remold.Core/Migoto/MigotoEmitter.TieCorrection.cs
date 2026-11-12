using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;

namespace Remold.Core.Migoto;

/// <summary>
/// The corrected tie. A bone a mesh cannot recover rides a stand-in's rows, which places everything
/// weighted to it as though it never moved against the stand-in. The mesh's own vertices on that bone
/// still record where the game put them, and that is enough to say how the bone differs from its
/// stand-in AT those vertices: the tied bone's rows gain a tail holding the smallest change that puts
/// them there. A vertex of the replacement that sits where one of the mesh's own sat, weighted as it was,
/// then lands where the game put it. One far from them takes a guess the tail cannot vouch for, which
/// <see cref="TieGroup.Reach"/> measures.
/// </summary>
public sealed partial class MigotoEmitter
{
    /// <summary>The least length (metres) the correction's solve measures a bone's positions in. A bone
    /// whose own vertices spread wider is measured in their spread, so a slope and an offset weigh alike
    /// across them; one whose vertices sit closer than this is measured in it, so their spread reads small
    /// and the cutoff below can drop it.</summary>
    const double CorrectionScale = 0.01;

    /// <summary>Singular-value cutoff (relative to σmax) of the correction's solve: own vertices spread
    /// under about a tenth of a millimetre state an offset and no slope.</summary>
    const double CorrectionRcond = 1e-2;

    /// <summary>How far a replacement vertex may lean on what a corrected tie cannot vouch for: its weight on
    /// the bone times its distance (metres) from what the bone's own vertices span. Past it the pipeline
    /// ships the plain tie for that bone.</summary>
    const double CorrectionReach = 0.0005;

    /// <summary>The weight on a plainly tied bone from which a replacement vertex counts as leaning on a bone
    /// the build cannot recover.</summary>
    const double TiedWeightNotice = 0.05;

    /// <summary>The tied bones of one mesh whose own vertices overlap, solved together because a shared
    /// vertex's miss cannot be split between them any other way. <see cref="Bones"/> are full local rows,
    /// ascending; <see cref="Centers"/> holds each bone's centre (three per bone), <see cref="Scales"/> the
    /// length its positions are measured in, and <see cref="Basis"/> <see cref="Rank"/> orthonormal rows of
    /// 4·|Bones|, spanning what the group's own vertices state about the bones' corrections.</summary>
    internal sealed record TieGroup(int[] Bones, double[] Centers, double[] Scales, double[] Basis, int Rank)
    {
        /// <summary>How much of one vertex's lean on this group's bones the group's own vertices do NOT
        /// state, in weight · metres: zero for a vertex in the span of a bone's own vertices carrying the
        /// weights they carry. <paramref name="lanes"/> are the vertex's influences as (full local row,
        /// weight); rows outside the group are ignored.</summary>
        public double Reach(double x, double y, double z, IEnumerable<(int Row, double Weight)> lanes)
        {
            int cols = 4 * Bones.Length;
            var a = new double[cols];
            bool any = false;
            foreach (var (row, weight) in lanes)
            {
                int j = Array.IndexOf(Bones, row);
                if (j < 0 || weight <= 0) continue;
                any = true;
                a[4 * j] += weight * (x - Centers[3 * j]) / Scales[j];
                a[4 * j + 1] += weight * (y - Centers[3 * j + 1]) / Scales[j];
                a[4 * j + 2] += weight * (z - Centers[3 * j + 2]) / Scales[j];
                a[4 * j + 3] += weight;
            }
            if (!any) return 0;
            var rest = (double[])a.Clone();
            for (int k = 0; k < Rank; k++)
            {
                double dot = 0;
                for (int c = 0; c < cols; c++) dot += a[c] * Basis[k * cols + c];
                for (int c = 0; c < cols; c++) rest[c] -= dot * Basis[k * cols + c];
            }
            // back to metres: what is left of a position by its bone's own length, and what is left of a
            // weight by the least length, as an offset between bones of that size
            double sum = 0;
            for (int c = 0; c < cols; c++)
            {
                double left = rest[c] * (c % 4 == 3 ? CorrectionScale : Scales[c / 4]);
                sum += left * left;
            }
            return Math.Sqrt(sum);
        }
    }

    /// <summary>What <see cref="CorrectTies"/> decided, indexed by full local bone. A corrected bone's tail
    /// is <see cref="TailVerts"/> and its four <see cref="TailRows"/>, appended to the tie's block that is
    /// <see cref="TieWidth"/> wide; <see cref="OwnVerts"/> counts the bone's own vertices the tail reads.</summary>
    sealed record TieCorrection(bool[] Corrected, int[] TieWidth, int[] GroupOf, int[][] TailVerts,
        double[][][] TailRows, int[] OwnVerts, List<TieGroup> Groups)
    {
        public static TieCorrection None(int nb) => new(new bool[nb], new int[nb],
            Enumerable.Repeat(-1, nb).ToArray(), new int[nb][], new double[nb][][], new int[nb], new List<TieGroup>());
    }

    static string CorrectionNote(TieCorrection correction, int b) => correction.Corrected[b]
        ? $" · corrected by {correction.OwnVerts[b]} of its own vertices, {correction.TailVerts[b].Length} more rows"
        : "";

    /// <summary>Give every tied output bone that can take one a correction tail.
    ///
    /// <para>For the tied bones J whose own vertices overlap, the reconstruction with each riding its
    /// stand-in misses the game's posed vertex v by <c>Σ_j w_vj · x_v · Δ_j</c>, where Δ_j is how bone j
    /// differs from its stand-in. The miss is a fixed combination of posed vertices (v itself, less what
    /// the other bones' rows rebuild), so the least Δ that explains every miss at the bones' own vertices
    /// is a fixed combination too, and it is folded into the bone's rows here. Positions are taken about
    /// each bone's own vertices, so the least change is an offset first and a slope only where those
    /// vertices are spread enough to state one.</para>
    ///
    /// <para>A bone keeps the plain tie when the tail would pass <paramref name="kCap"/> rows with the
    /// tie's, when a bone it shares a vertex with ships at every vertex of the mesh, or when the corrected
    /// rows would hand a vertex of the mesh more than <see cref="SlimGainGate"/> times its input's
    /// rounding.</para></summary>
    static TieCorrection CorrectTies(StreamsLoad load, int nb, IReadOnlyList<int> outputRows,
        Func<int, bool> tied, int[] tie, bool[] weak, int[][] picked, double[][][] rows, bool[] denseWidth,
        Func<int[], SlimSolve> solveMissing, int kCap)
    {
        var result = TieCorrection.None(nb);
        int n = load.P.GetLength(0);
        var joint = new SortedSet<int>(outputRows.Where(tied));
        if (joint.Count == 0) return result;

        double WeightOn(int v, int bone)
        {
            double sum = 0;
            for (int k = 0; k < 4; k++) if (load.BI[v, k] == bone && load.W[v, k] > 0) sum += load.W[v, k];
            return sum;
        }

        // each tied bone's own vertices, and every tied bone that shares one: a shared vertex's miss belongs
        // to all the tied bones on it, output or not
        var own = new Dictionary<int, int[]>();
        while (true)
        {
            foreach (int b in joint)
                if (!own.ContainsKey(b))
                    own[b] = PoolMath.SelectAnchorRows(load.P, load.W, load.BI, b, Math.Min(KStart, n), 0.0);
            var found = new List<int>();
            foreach (int b in joint)
                foreach (int v in own[b])
                    for (int k = 0; k < 4; k++)
                    {
                        int c = load.BI[v, k];
                        if (load.W[v, k] > 0 && c >= 0 && c < nb && !joint.Contains(c) && tied(c)) found.Add(c);
                    }
            if (found.Count == 0) break;
            foreach (int c in found) joint.Add(c);
        }
        foreach (int b in joint.Where(b => own[b].Length == 0).ToList()) joint.Remove(b);
        if (joint.Count == 0) return result;

        // groups: tied bones joined through a vertex either of them reads
        var parent = joint.ToDictionary(b => b, b => b);
        int Find(int b) { while (parent[b] != b) b = parent[b] = parent[parent[b]]; return b; }
        var verts = new SortedSet<int>(joint.SelectMany(b => own[b]));
        foreach (int v in verts)
        {
            int first = -1;
            for (int k = 0; k < 4; k++)
            {
                int c = load.BI[v, k];
                if (load.W[v, k] <= 0 || !joint.Contains(c)) continue;
                if (first < 0) first = Find(c);
                else parent[Find(c)] = first;
            }
        }

        // The rows each bone on those vertices is rebuilt with while every tied bone rides its stand-in. An
        // output bone's block already holds its stand-in's; a tied bone outside the output takes its
        // stand-in's; a sound one outside the output is solved here.
        var missing = new SortedSet<int>();
        int Source(int c) => picked[c] is null && weak[c] && tie[c] >= 0 ? tie[c] : c;
        foreach (int v in verts)
            for (int k = 0; k < 4; k++)
            {
                int c = load.BI[v, k];
                if (load.W[v, k] <= 0 || c < 0 || c >= nb) continue;
                if (picked[Source(c)] is null) missing.Add(Source(c));
            }
        if (missing.Count > 0)
        {
            var extra = solveMissing(missing.ToArray());
            foreach (int c in missing)
            {
                picked[c] = extra.Picked[c];
                rows[c] = extra.Rows[c];
                denseWidth[c] = extra.DenseWidth[c];
            }
        }

        // every vertex each tied bone carries, read once for the measure further down
        var carried = joint.ToDictionary(b => b, _ => new List<int>());
        for (int v = 0; v < n; v++)
            for (int k = 0; k < 4; k++)
                if (load.W[v, k] > 0 && carried.TryGetValue(load.BI[v, k], out var list)
                    && (list.Count == 0 || list[^1] != v)) list.Add(v);

        var scratch = new double[n];
        foreach (var members in joint.GroupBy(Find).Select(g => g.OrderBy(b => b).ToArray())
                     .OrderBy(g => g[0]))
        {
            var groupVerts = new SortedSet<int>(members.SelectMany(b => own[b])).ToArray();
            int m = groupVerts.Length, q = members.Length;
            var centers = new double[3 * q];
            var scales = new double[q];
            for (int j = 0; j < q; j++)
            {
                foreach (int v in own[members[j]])
                    for (int d = 0; d < 3; d++) centers[3 * j + d] += load.P[v, d];
                for (int d = 0; d < 3; d++) centers[3 * j + d] /= own[members[j]].Length;
                double spread = 0;
                foreach (int v in own[members[j]])
                    for (int d = 0; d < 3; d++)
                        spread += (load.P[v, d] - centers[3 * j + d]) * (load.P[v, d] - centers[3 * j + d]);
                scales[j] = Math.Max(CorrectionScale, Math.Sqrt(spread / own[members[j]].Length));
            }

            // one equation per vertex, scaled so its heaviest tied bone reads 1: a faintly weighted vertex
            // then states its bones as firmly as a heavy one, and the cutoff below judges spread, not weight
            var a = new double[m, 4 * q];
            var scale = new double[m];
            for (int i = 0; i < m; i++)
            {
                int v = groupVerts[i];
                double heaviest = 0;
                for (int j = 0; j < q; j++) heaviest = Math.Max(heaviest, WeightOn(v, members[j]));
                scale[i] = heaviest > 0 ? 1 / heaviest : 0;
                for (int j = 0; j < q; j++)
                {
                    double wv = WeightOn(v, members[j]) * scale[i];
                    if (wv <= 0) continue;
                    for (int d = 0; d < 3; d++)
                        a[i, 4 * j + d] = wv * (load.P[v, d] - centers[3 * j + d]) / scales[j];
                    a[i, 4 * j + 3] = wv;
                }
            }
            var svd = Matrix<double>.Build.DenseOfArray(a).Svd(computeVectors: true);
            double top = svd.S.Count > 0 ? svd.S.Maximum() : 0;
            var kept = Enumerable.Range(0, Math.Min(m, 4 * q)).Where(k => svd.S[k] > CorrectionRcond * top && top > 0)
                .ToArray();
            if (kept.Length == 0) continue;
            int cols = 4 * q;
            var basis = new double[kept.Length * cols];
            for (int k = 0; k < kept.Length; k++)
                for (int c = 0; c < cols; c++) basis[k * cols + c] = svd.VT[kept[k], c];
            // pinv[c, i] = Σ_k V[c,k] / σ_k · U[i,k]
            var pinv = new double[cols, m];
            foreach (int k in kept)
                for (int c = 0; c < cols; c++)
                {
                    double vc = svd.VT[k, c] / svd.S[k];
                    if (vc == 0) continue;
                    for (int i = 0; i < m; i++) pinv[c, i] += vc * svd.U[i, k];
                }

            // each vertex's rebuild with every tied bone riding its stand-in, as coefficients over the posed
            // vertices its bones' rows read
            var rebuild = new Dictionary<int, double>[m];
            bool everyVertex = false;
            for (int i = 0; i < m && !everyVertex; i++)
            {
                int v = groupVerts[i];
                var acc = rebuild[i] = new Dictionary<int, double>();
                for (int k = 0; k < 4; k++)
                {
                    int c = load.BI[v, k];
                    double wv = load.W[v, k];
                    if (wv <= 0 || c < 0 || c >= nb) continue;
                    int s = Source(c);
                    if (denseWidth[s] || picked[s].Length >= n) { everyVertex = true; break; }
                    var sel = picked[s];
                    var r = rows[s];
                    for (int t = 0; t < sel.Length; t++)
                    {
                        double term = wv * (load.P[v, 0] * r[0][t] + load.P[v, 1] * r[1][t]
                                            + load.P[v, 2] * r[2][t] + r[3][t]);
                        acc[sel[t]] = acc.GetValueOrDefault(sel[t]) + term;
                    }
                }
            }
            if (everyVertex) continue;

            var tails = new Dictionary<int, SortedDictionary<int, double[]>>();
            foreach (int b in members)
            {
                if (!outputRows.Contains(b) || !tied(b)) continue;
                int j = Array.IndexOf(members, b);
                var tail = new SortedDictionary<int, double[]>();
                double[] Entry(int v) => tail.TryGetValue(v, out var e) ? e : tail[v] = new double[4];
                for (int i = 0; i < m; i++)
                {
                    // the bone's four rows over this vertex's miss, restated from centred, scaled positions
                    var coef = new double[4];
                    for (int d = 0; d < 3; d++) coef[d] = pinv[4 * j + d, i] * scale[i] / scales[j];
                    coef[3] = pinv[4 * j + 3, i] * scale[i];
                    for (int d = 0; d < 3; d++) coef[3] -= centers[3 * j + d] * coef[d];
                    if (coef[0] == 0 && coef[1] == 0 && coef[2] == 0 && coef[3] == 0) continue;
                    var at = Entry(groupVerts[i]);
                    for (int r = 0; r < 4; r++) at[r] += coef[r];
                    foreach (var (u, term) in rebuild[i])
                    {
                        var e = Entry(u);
                        for (int r = 0; r < 4; r++) e[r] -= coef[r] * term;
                    }
                }
                if (tail.Count == 0 || picked[b].Length + tail.Count > kCap) continue;
                tails[b] = tail;
            }

            // what the corrected rows hand a vertex of the mesh: every vertex a corrected bone carries is
            // rebuilt with the tails in place, and its coefficients over the posed vertices are measured
            foreach (int b in tails.Keys.OrderBy(b => b).ToList())
            {
                bool loud = false;
                var touched = new List<int>();
                foreach (int v in carried[b])
                {
                    if (loud) break;
                    touched.Clear();
                    bool measurable = true;
                    for (int k = 0; k < 4 && measurable; k++)
                    {
                        int c = load.BI[v, k];
                        double wv = load.W[v, k];
                        if (wv <= 0 || c < 0 || c >= nb) continue;
                        int s = Source(c);
                        if (picked[s] is null) { measurable = false; break; }
                        void Add(int u, double r0, double r1, double r2, double r3)
                        {
                            if (scratch[u] == 0) touched.Add(u);
                            scratch[u] += wv * (load.P[v, 0] * r0 + load.P[v, 1] * r1 + load.P[v, 2] * r2 + r3);
                        }
                        var sel = picked[s];
                        var r = rows[s];
                        for (int t = 0; t < sel.Length; t++) Add(sel[t], r[0][t], r[1][t], r[2][t], r[3][t]);
                        if (tails.TryGetValue(c, out var tail))
                            foreach (var (u, e) in tail) Add(u, e[0], e[1], e[2], e[3]);
                    }
                    double sum = 0;
                    foreach (int u in touched) { sum += scratch[u] * scratch[u]; scratch[u] = 0; }
                    // a vertex whose other bones were never solved here is one the output does not ship
                    if (measurable && FailsGainGate(Math.Sqrt(sum))) loud = true;
                }
                if (loud) tails.Remove(b);
            }
            if (tails.Count == 0) continue;

            int id = result.Groups.Count;
            result.Groups.Add(new TieGroup(members, centers, scales, basis, kept.Length));
            foreach (var (b, tail) in tails)
            {
                result.Corrected[b] = true;
                result.TieWidth[b] = picked[b].Length;
                result.GroupOf[b] = id;
                result.OwnVerts[b] = groupVerts.Count(v => WeightOn(v, b) > 0);
                result.TailVerts[b] = tail.Keys.ToArray();
                result.TailRows[b] = Enumerable.Range(0, 4)
                    .Select(r => tail.Values.Select(e => e[r]).ToArray()).ToArray();
            }
        }
        return result;
    }
}
