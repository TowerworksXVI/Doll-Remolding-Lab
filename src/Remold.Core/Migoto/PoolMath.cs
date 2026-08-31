using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Factorization;

namespace Remold.Core.Migoto;

/// <summary>
/// The numeric core of the pooled whole-body mesh swap: the linear-blend-skinning (LBS) coefficient
/// matrix, its pseudoinverse (the per-frame palette-recovery operator), the union-bone reconciliation
/// across pooled parts, and the identity-body concatenation.
///
/// <para>Skinning is linear in the bone matrices, so the posed vertex buffer is <c>q = C x</c> with
/// C built only from bind positions and weights (constant every frame) and x the packed bone-matrix
/// columns. <see cref="PInv"/> precomputes C+ once; the GPU recover shader applies it per frame.</para>
/// </summary>
public static class PoolMath
{
    /// <summary>Scatter-map entry for a bone this part does NOT own — the recover shader skips writing it.</summary>
    public const uint Sentinel = 0xFFFFFFFF;

    // ---- buffer parsing (stride-40 stream0 pos@0, stride-32 stream2 weight@0 index@16) ----------

    /// <summary>Parse positions from a vertex stream: n = len/stride, three floats at each
    /// <c>i*stride + offset</c>, widened to double.</summary>
    public static double[,] ParsePositions(byte[] buf, int stride, int offset)
    {
        int cnt = buf.Length / stride;
        var p = new double[cnt, 3];
        for (int i = 0; i < cnt; i++)
        {
            int o = i * stride + offset;
            p[i, 0] = BitConverter.ToSingle(buf, o);
            p[i, 1] = BitConverter.ToSingle(buf, o + 4);
            p[i, 2] = BitConverter.ToSingle(buf, o + 8);
        }
        return p;
    }

    /// <summary>A stride-40 vertex stream (position f3 @0, normal f3 @12, tangent f4 @24) restated in
    /// another bind space: each directional triple rotates by <paramref name="rot"/> (row-vector
    /// <c>v·R</c>), the tangent's handedness w passes through. <paramref name="rot"/> must be a SNAPPED
    /// delta — a signed permutation with no translation — so each rewritten float is an original one with
    /// its component permuted and its sign flipped, and points need no separate treatment from
    /// directions.</summary>
    public static byte[] RotateVertexStream(byte[] stream0, Matrix4x4 rot)
    {
        if (stream0.Length % 40 != 0)
            throw new ArgumentException($"stream0 is {stream0.Length} bytes, not a whole number of stride-40 vertices");
        var outb = (byte[])stream0.Clone();
        for (int o = 0; o < outb.Length; o += 40)
        {
            Rotate3(stream0, outb, o, rot);        // position
            Rotate3(stream0, outb, o + 12, rot);   // normal
            Rotate3(stream0, outb, o + 24, rot);   // tangent xyz (w at +36 untouched)
        }
        return outb;
    }

    static void Rotate3(byte[] src, byte[] dst, int o, Matrix4x4 r)
    {
        float x = BitConverter.ToSingle(src, o), y = BitConverter.ToSingle(src, o + 4), z = BitConverter.ToSingle(src, o + 8);
        BitConverter.GetBytes(x * r.M11 + y * r.M21 + z * r.M31).CopyTo(dst, o);
        BitConverter.GetBytes(x * r.M12 + y * r.M22 + z * r.M32).CopyTo(dst, o + 4);
        BitConverter.GetBytes(x * r.M13 + y * r.M23 + z * r.M33).CopyTo(dst, o + 8);
    }

    /// <summary>Parse stream2: BlendWeight f32x4 @0 (widened to double), BlendIndices u32x4 @16 (as int).</summary>
    public static (double[,] W, int[,] BI) ParseSkin(byte[] buf, int stride = 32)
    {
        int n = buf.Length / stride;
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int i = 0; i < n; i++)
        {
            int o = i * stride;
            for (int k = 0; k < 4; k++) w[i, k] = BitConverter.ToSingle(buf, o + k * 4);
            for (int k = 0; k < 4; k++) bi[i, k] = unchecked((int)BitConverter.ToUInt32(buf, o + 16 + k * 4));
        }
        return (w, bi);
    }

    // ---- the shared coefficient matrix C (N x 4*nbones), constant across frames -----------------

    /// <summary>Build C (N x 4*nbones): each vertex row accumulates <c>w*[px,py,pz,1]</c> into the
    /// 4-column block of every influencing bone.</summary>
    public static double[,] BuildC(double[,] p, double[,] w, int[,] bi, int nbones)
    {
        int n = p.GetLength(0);
        var C = new double[n, 4 * nbones];
        for (int v = 0; v < n; v++)
        {
            double px = p[v, 0], py = p[v, 1], pz = p[v, 2];
            for (int k = 0; k < 4; k++)
            {
                double wk = w[v, k];
                if (wk == 0.0) continue;
                int b = bi[v, k];
                if (b < 0 || b >= nbones) continue;
                int c = 4 * b;
                C[v, c] += wk * px;
                C[v, c + 1] += wk * py;
                C[v, c + 2] += wk * pz;
                C[v, c + 3] += wk;
            }
        }
        return C;
    }

    /// <summary>
    /// The Moore-Penrose pseudoinverse of C held in FACTORED form — the shape the build actually consumes
    /// it in. Materializing the m×n matrix costs an m×m·n product and 8·m·n bytes; the callers that need a
    /// product against it (<see cref="Apply"/>) or a handful of its rows (<see cref="Row"/>) get them from
    /// the factors for a fraction of both. <see cref="Materialize"/> is for the caller that ships the whole
    /// operator.
    ///
    /// <para>pinv is <see cref="Rows"/>×<see cref="Cols"/> = (columns of C)×(rows of C).</para>
    /// </summary>
    public sealed class PInvFactors
    {
        readonly Matrix<double> _q;       // thin Q of whichever orientation was factored
        readonly Matrix<double> _pinvR;   // pinv of that factorization's R
        readonly bool _wide;              // C was wide, so Cᵀ was factored and pinv(C) = Q·pinv(R)ᵀ

        internal PInvFactors(Matrix<double> q, Matrix<double> pinvR, bool wide, int rows, int cols)
        {
            _q = q; _pinvR = pinvR; _wide = wide;
            Rows = rows; Cols = cols;
        }

        /// <summary>Rows of pinv(C) — the columns of C (4·bones).</summary>
        public int Rows { get; }

        /// <summary>Columns of pinv(C) — the rows of C (vertices).</summary>
        public int Cols { get; }

        /// <summary>
        /// <c>float32(pinv(C))·rhs</c> for a <see cref="Cols"/>×k right-hand side, a row block at a time so
        /// the whole m×n pinv is never held.
        ///
        /// <para>The float32 rounding is PART OF THE PRODUCT, not an implementation detail: an
        /// ill-conditioned row's coefficients are large, so rounding them to the width they ship at moves
        /// the product by O(1) where the float64 product would be exact. A caller measuring what the shipped
        /// operator recovers has to multiply by the shipped coefficients.</para>
        /// </summary>
        public double[,] ApplyAsFloat32(double[,] rhs)
        {
            int k = rhs.GetLength(1);
            var result = new double[Rows, k];
            for (int r0 = 0; r0 < Rows; r0 += RowBlock)
            {
                int cnt = Math.Min(RowBlock, Rows - r0);
                var block = RowBlockOf(r0, cnt).ToArray();    // cnt x Cols, float64, row-major
                for (int i = 0; i < cnt; i++)
                    for (int j = 0; j < k; j++)
                    {
                        double acc = 0;
                        for (int c = 0; c < Cols; c++) acc += (float)block[i, c] * rhs[c, j];
                        result[r0 + i, j] = acc;
                    }
            }
            return result;
        }

        /// <summary>Rows of pinv materialized per pass of <see cref="ApplyAsFloat32"/>. The block is
        /// <c>8·RowBlock·Cols</c> bytes — 25 MB at a full-size mesh — which keeps several operators solving
        /// at once well inside memory, while being wide enough that the per-product setup (the factors are
        /// re-read for every block) is amortised.</summary>
        const int RowBlock = 128;

        /// <summary><paramref name="count"/> rows of pinv(C) starting at <paramref name="r0"/>.</summary>
        Matrix<double> RowBlockOf(int r0, int count) => _wide
            ? _q.SubMatrix(r0, count, 0, _q.ColumnCount).TransposeAndMultiply(_pinvR)   // (Q·pinv(R)ᵀ)[block,·]
            : _pinvR.SubMatrix(r0, count, 0, _pinvR.ColumnCount).TransposeAndMultiply(_q);

        /// <summary>Row <paramref name="r"/> of pinv(C) (length <see cref="Cols"/>), without forming pinv.</summary>
        public double[] Row(int r) => _wide
            ? _pinvR.Multiply(_q.Row(r)).ToArray()      // (Q·pinv(R)ᵀ)[r,·]
            : _q.Multiply(_pinvR.Row(r)).ToArray();     // (pinv(R)·Qᵀ)[r,·]

        /// <summary>The whole pinv, row-major float32 (<c>Rows*Cols</c>) — the exact byte order the recover
        /// operator consumes.</summary>
        public float[] Materialize()
        {
            var pinv = _pinvR.TransposeAndMultiply(_q);   // pinv(R)·Qᵀ of whichever orientation was factored
            var outf = new float[Rows * Cols];
            if (_wide)
                for (int i = 0; i < Rows; i++)
                    for (int j = 0; j < Cols; j++)
                        outf[i * Cols + j] = (float)pinv[j, i];
            else
                for (int i = 0; i < Rows; i++)
                    for (int j = 0; j < Cols; j++)
                        outf[i * Cols + j] = (float)pinv[i, j];
            return outf;
        }
    }

    /// <summary>
    /// Factor C for its Moore-Penrose pseudoinverse, matching <c>np.linalg.pinv(C, rcond)</c>: singular
    /// values ≤ rcond·σmax are zeroed.
    /// </summary>
    /// <remarks>
    /// For a tall C (verts ≫ 4·bones) the full-U SVD is impractical, so the pinv is factored as a thin QR
    /// (C = Q·R, Q n×m, R m×m) followed by the SVD of the small square R: pinv(C) = pinv(R)·Qᵀ with
    /// pinv(R) = V·diag(σ⁺)·Uᵀ, σ⁺_r = 1/σ_r when σ_r &gt; rcond·σmax else 0. QR preserves the singular
    /// values exactly, so the rcond cutoff is identical to numpy's. Being a different LA backend, the
    /// float64 results differ from numpy's at the last bit — same residual, same recover floor, so
    /// byte-parity is not worth chasing.
    /// </remarks>
    public static PInvFactors Factor(double[,] C, double rcond = 1e-8)
    {
        int n = C.GetLength(0);          // rows (verts)
        int m = C.GetLength(1);          // cols (4*nbones)

        // The thin-QR path needs a tall matrix (rows ≥ cols). Real C is always tall (verts ≫ 4·bones);
        // for a rare wide C, factor the transpose: pinv(C) = pinv(Cᵀ)ᵀ.
        if (n < m)
        {
            var t = new double[m, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < m; j++) t[j, i] = C[i, j];
            var (qt, pinvRt) = FactorTall(Matrix<double>.Build.DenseOfArray(t), rcond);
            return new PInvFactors(qt, pinvRt, wide: true, rows: m, cols: n);
        }

        var (q, pinvR) = FactorTall(Matrix<double>.Build.DenseOfArray(C), rcond);
        return new PInvFactors(q, pinvR, wide: false, rows: m, cols: n);
    }

    /// <summary>Thin QR of a tall A plus the pseudoinverse of its R, the pair every pinv consumer works
    /// from.</summary>
    static (Matrix<double> Q, Matrix<double> PinvR) FactorTall(Matrix<double> A, double rcond)
    {
        int m = A.ColumnCount;
        var qr = A.QR(QRMethod.Thin);
        var Q = qr.Q;                    // n x m (thin)
        var R = qr.R;                    // m x m

        var svd = R.Svd(computeVectors: true);
        var s = svd.S;                   // singular values of R == singular values of A
        var U = svd.U;                   // m x m
        var Vt = svd.VT;                 // m x m
        double smax = 0.0;
        for (int r = 0; r < m; r++) if (s[r] > smax) smax = s[r];
        double cutoff = rcond * smax;

        // pinv(R) = V * diag(sinv) * Uᵀ  — scale V's columns by sinv, then multiply by Uᵀ
        var V = Vt.Transpose();          // m x m
        for (int r = 0; r < m; r++)
        {
            double sinv = s[r] > cutoff ? 1.0 / s[r] : 0.0;
            for (int i = 0; i < m; i++) V[i, r] *= sinv;
        }
        return (Q, V.TransposeAndMultiply(U));     // (V diag(sinv)) * Uᵀ   (m x m)
    }

    /// <summary>The whole pseudoinverse as row-major float32 (m*n, m = 4*nbones cols of C, n = its rows).
    /// Computed in float64 then truncated to float32, as numpy does.</summary>
    public static float[] PInv(double[,] C, double rcond = 1e-8) => Factor(C, rcond).Materialize();

    // ---- slim anchor-row selection (operator slimming) ------------------------------------------

    /// <summary>Pick up to <paramref name="k"/> anchor vertices for one bone. Candidates are every vertex
    /// weighted to it, ranked by that weight with a vertex-index tie-break — the emitted operator bytes are
    /// a contract, so the pick must be fully deterministic. The pick is the highest-weight candidate
    /// followed by a greedy farthest-point spread in bind space: rank alone clusters on a seam and loses
    /// rank-4 support. Returned ascending; the set, not the order, matters.</summary>
    public static int[] SelectAnchorRows(double[,] p, double[,] w, int[,] bi, int bone, int k)
    {
        int n = p.GetLength(0);
        var cand = new List<(double W, int V)>();
        for (int v = 0; v < n; v++)
        {
            double wb = 0;
            for (int j = 0; j < 4; j++) if (bi[v, j] == bone && w[v, j] > wb) wb = w[v, j];
            if (wb > 0) cand.Add((wb, v));
        }
        if (cand.Count == 0) return Array.Empty<int>();
        cand.Sort((a, b) => a.W != b.W ? b.W.CompareTo(a.W) : a.V.CompareTo(b.V));
        int pool = Math.Min(cand.Count, 4 * k);   // spread over the top-weighted candidates only
        int take = Math.Min(cand.Count, k);
        var used = new bool[pool];
        var minD = new double[pool];
        var sel = new List<int>(take) { cand[0].V };
        used[0] = true;
        for (int i = 0; i < pool; i++) minD[i] = Dist2(p, cand[i].V, cand[0].V);
        while (sel.Count < take)
        {
            int best = -1;
            double bestD = -1;
            for (int i = 0; i < pool; i++)
            {
                if (used[i]) continue;
                if (minD[i] > bestD) { bestD = minD[i]; best = i; }   // ties: first (highest weight)
            }
            if (best < 0) break;
            used[best] = true;
            sel.Add(cand[best].V);
            for (int i = 0; i < pool; i++)
                if (!used[i]) minD[i] = Math.Min(minD[i], Dist2(p, cand[i].V, cand[best].V));
        }
        sel.Sort();
        return sel.ToArray();
    }

    static double Dist2(double[,] p, int a, int b)
    {
        double d = 0;
        for (int j = 0; j < 3; j++) { double dd = p[a, j] - p[b, j]; d += dd * dd; }
        return d;
    }

    /// <summary>Weight below which a vertex counts as carrying NO influence from the target bone, for
    /// discriminator purposes. A discriminator must pin co-bone columns without adding target-bone signal;
    /// a trace influence would do the second. Internal because it parameterises the solve, and the operator
    /// cache stamps every such constant into the identity an entry is valid under.</summary>
    internal const double DiscriminatorTargetFloor = 1e-4;

    /// <summary>Pick up to <paramref name="kd"/> DISCRIMINATOR vertices for one bone: vertices weighted on
    /// the co-bones of <paramref name="alreadySelected"/> but carrying no target-bone weight of their own.
    ///
    /// <para>A bone whose whole anchor region is proportionally co-weighted with a neighbour is locally
    /// indistinguishable from it — the anchor rows alone cannot say which of the two moved. These rows
    /// constrain the co-bone columns without adding a row of the target's own signal, which is what lets
    /// the local solve separate them. <see cref="LocalPInvRows"/> takes them unchanged: it derives its kept
    /// co-bones from mass over the whole selection, and these rows raise exactly that mass.</para>
    ///
    /// <para>Co-bones are whatever is weighted within <paramref name="alreadySelected"/> besides the target,
    /// ranked by summed mass there (bone-index tie-break). The pick round-robins over that order, each
    /// co-bone contributing its highest-weight unused candidate (ascending vertex index on ties), so a
    /// budget too small to serve every co-bone still spreads across the strongest ones. Vertices already in
    /// the selection are excluded. Returned ascending; the set, not the order, matters.</para>
    ///
    /// <para>Influences whose bone index falls outside <paramref name="nbones"/> are not co-bones, as
    /// <see cref="BuildC"/> and <see cref="LocalPInvRows"/> give them no column: rows picked to pin a column
    /// that does not exist buy the bone nothing but width.</para></summary>
    public static int[] SelectDiscriminatorRows(double[,] p, double[,] w, int[,] bi, int bone,
        int[] alreadySelected, int nbones, int kd)
    {
        if (kd <= 0) return Array.Empty<int>();
        int n = p.GetLength(0);

        var mass = new Dictionary<int, double>();
        foreach (int v in alreadySelected)
            for (int j = 0; j < 4; j++)
            {
                if (w[v, j] <= 0.0) continue;
                int b = bi[v, j];
                if (b == bone || b < 0 || b >= nbones) continue;
                mass[b] = mass.GetValueOrDefault(b) + w[v, j];
            }
        if (mass.Count == 0) return Array.Empty<int>();

        var taken = new HashSet<int>(alreadySelected);
        var order = mass.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray();

        // per co-bone: its candidate vertices, best weight first. A vertex qualifies only when the target's
        // own weight on it is negligible — otherwise the row carries target signal and discriminates nothing.
        var queues = new List<(double W, int V)>[order.Length];
        var pos = new int[order.Length];
        for (int c = 0; c < order.Length; c++)
        {
            int cb = order[c];
            var cand = new List<(double W, int V)>();
            for (int v = 0; v < n; v++)
            {
                if (taken.Contains(v)) continue;
                double wc = 0, wt = 0;
                for (int j = 0; j < 4; j++)
                {
                    if (w[v, j] <= 0.0) continue;
                    if (bi[v, j] == cb && w[v, j] > wc) wc = w[v, j];
                    if (bi[v, j] == bone && w[v, j] > wt) wt = w[v, j];
                }
                if (wc > 0 && wt < DiscriminatorTargetFloor) cand.Add((wc, v));
            }
            cand.Sort((a, b) => a.W != b.W ? b.W.CompareTo(a.W) : a.V.CompareTo(b.V));
            queues[c] = cand;
        }

        var picked = new List<int>(kd);
        var used = new HashSet<int>();
        bool progress = true;
        while (picked.Count < kd && progress)
        {
            progress = false;
            for (int c = 0; c < order.Length && picked.Count < kd; c++)
            {
                var q = queues[c];
                while (pos[c] < q.Count && used.Contains(q[pos[c]].V)) pos[c]++;
                if (pos[c] >= q.Count) continue;
                int v = q[pos[c]++].V;
                used.Add(v);
                picked.Add(v);
                progress = true;
            }
        }
        picked.Sort();
        return picked.ToArray();
    }

    /// <summary>The four slim operator rows of <paramref name="bone"/> from a LOCAL least squares
    /// over the selected vertices, and the rows' LEFT-INVERSE DEFECT — the pose-free quantity the
    /// operator actually needs bounded.
    ///
    /// <para>The solve restricts <see cref="BuildC"/>'s columns two ways. Bones with no weight in
    /// the selection are absent outright (a full-width pseudoinverse would couple the min-norm
    /// solution to bones the selection can't see). And of the bones that ARE weighted, the target
    /// plus the strongest co-bones by mass keep columns up to a cap ≤ the determinacy bound
    /// 4·|kept| ≤ |sel| — small selections may run merely determined or (under 4 rows)
    /// underdetermined, which the defect exposes. Real meshes need BOTH cap directions: some bones
    /// fail without a wide cap (their strongest co-bones are load-bearing) and others fail WITH one
    /// (near-dependent extra columns worsen their conditioning), so <paramref name="capDivisor"/>
    /// selects the level and the caller searches the levels per bone, gated by the defect. A
    /// dropped co-bone's contribution is never silent: it lands in the returned defect.</para>
    ///
    /// <para>Influences whose bone index falls outside <paramref name="nbones"/> are dropped, as
    /// <see cref="BuildC"/> drops them: they are not columns of the system being inverted, so they must not
    /// become columns of the model that measures it.</para>
    ///
    /// <para><paramref name="Deviation"/> = max over the target's 4 rows y of
    /// <c>‖yᵀ·C_all[sel,·] − e_row‖∞</c>, where C_all carries EVERY bone weighted in the selection, dropped
    /// columns included. Zero means the rows are a true left inverse over the selection — exact recovery for
    /// ANY palette, with a real pose's error bounded by the defect times the palette's row magnitudes. A
    /// single-pose probe cannot certify that: a rank-truncated solve can be near-identity along the probe and
    /// wrong elsewhere, which is why the defect, not a pose residual, is the gate.</para></summary>
    public static (double[][] Rows, double Deviation) LocalPInvRows(double[,] p, double[,] w, int[,] bi,
        int bone, int[] sel, int nbones, int capDivisor = 4, double rcond = 1e-8)
    {
        var rows = new double[4][];
        for (int r = 0; r < 4; r++) rows[r] = new double[sel.Length];

        // Every bone weighted within the selection, with its summed support mass. An influence counts here
        // exactly when BuildC would give it a column — same zero test, same palette-range test — so the
        // defect model and the system it measures never disagree about a malformed dump's vertices.
        var mass = new Dictionary<int, double>();
        foreach (int v in sel)
            for (int j = 0; j < 4; j++)
            {
                if (w[v, j] == 0.0) continue;
                int b = bi[v, j];
                if (b < 0 || b >= nbones) continue;
                mass[b] = mass.GetValueOrDefault(b) + w[v, j];
            }
        if (!mass.ContainsKey(bone)) return (rows, double.PositiveInfinity);   // no support in the selection

        // solve columns: the target, then co-bones by descending mass (bone-index tie-break), stopping at
        // the level's cap. No mass floor: dropping a small-but-real co-bone puts its whole contribution into
        // the defect — measured pushing marginal bones just over the gate — while trace columns are
        // zeroed by the rcond cutoff anyway.
        int maxKept = Math.Max(1, sel.Length / capDivisor);
        var kept = new HashSet<int> { bone };
        foreach (var kv in mass.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
        {
            if (kept.Count >= maxKept) break;
            kept.Add(kv.Key);
        }

        var localOf = new Dictionary<int, int>();   // solve-column index, deterministic insertion order
        localOf[bone] = 0;
        foreach (var kv in mass.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            if (kept.Contains(kv.Key) && !localOf.ContainsKey(kv.Key)) localOf[kv.Key] = localOf.Count;
        var allOf = new Dictionary<int, int>();     // defect columns: every weighted bone
        foreach (var kv in mass.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            allOf[kv.Key] = allOf.Count;

        var pl = new double[sel.Length, 3];
        var wl = new double[sel.Length, 4];
        var bl = new int[sel.Length, 4];
        var wa = new double[sel.Length, 4];
        var ba = new int[sel.Length, 4];
        for (int i = 0; i < sel.Length; i++)
        {
            int v = sel[i];
            for (int j = 0; j < 3; j++) pl[i, j] = p[v, j];
            for (int j = 0; j < 4; j++)
            {
                bool weighted = w[v, j] != 0.0 && bi[v, j] >= 0 && bi[v, j] < nbones;
                bool keptCol = weighted && kept.Contains(bi[v, j]);
                wl[i, j] = keptCol ? w[v, j] : 0;
                bl[i, j] = keptCol ? localOf[bi[v, j]] : 0;
                wa[i, j] = weighted ? w[v, j] : 0;
                ba[i, j] = weighted ? allOf[bi[v, j]] : 0;
            }
        }
        var cp = PInv(BuildC(pl, wl, bl, localOf.Count), rcond);
        int lb = localOf[bone];
        for (int r = 0; r < 4; r++)
            for (int t = 0; t < sel.Length; t++)
                rows[r][t] = cp[(4 * lb + r) * sel.Length + t];

        // the left-inverse defect against the FULL selection columns (float32 rows: what ships)
        var Call = BuildC(pl, wa, ba, allOf.Count);
        int ab = allOf[bone];
        double dev = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4 * allOf.Count; c++)
            {
                double acc = 0;
                for (int t = 0; t < sel.Length; t++) acc += (float)rows[r][t] * Call[t, c];
                dev = Math.Max(dev, Math.Abs(acc - (c == 4 * ab + r ? 1.0 : 0.0)));
            }
        return (rows, dev);
    }

    // ---- union bone order + per-part local->union maps ------------------------------------------

    /// <summary>One pooled part's inputs for union reconciliation.</summary>
    public sealed record UnionInput(uint[] Hashes, IReadOnlyDictionary<uint, double[]> Binds, byte[] Stream2);

    /// <summary>Union reconciliation result. <see cref="FullMaps"/>[p][b] is the union slot of local
    /// bone b (remaps vertex bone indices); <see cref="ScatterMaps"/>[p][b] is that slot only when the
    /// part OWNS the bone else <see cref="Sentinel"/>; <see cref="Owner"/>[u] is the owning part index.</summary>
    public sealed record UnionResult(uint[] UnionHashes, uint[][] FullMaps, uint[][] ScatterMaps, int[] Owner);

    /// <summary>A stable projection of a union onto the rows the shipped palettes retain.
    /// <see cref="SourceRows"/> are the original union indices in their original order;
    /// <see cref="OldToCompact"/> maps every original slot to its shipped slot, or -1 when pruned.</summary>
    public sealed record UnionCompaction(UnionResult Union, int[] SourceRows, int[] OldToCompact);

    /// <summary>Filter a reconciled union without changing the relative order of any surviving row. Local
    /// maps keep their original length because they are indexed by source bone tables; a pruned local row
    /// maps to <see cref="Sentinel"/>. Ownership remains stated in the original pool-part index space.</summary>
    public static UnionCompaction CompactUnion(UnionResult source, IEnumerable<int> retainedRows)
    {
        var rows = retainedRows.Distinct().OrderBy(i => i).ToArray();
        var oldToCompact = Enumerable.Repeat(-1, source.UnionHashes.Length).ToArray();
        for (int i = 0; i < rows.Length; i++)
        {
            int old = rows[i];
            if (old < 0 || old >= source.UnionHashes.Length)
                throw new ArgumentOutOfRangeException(nameof(retainedRows), old,
                    $"union row {old} is outside 0..{source.UnionHashes.Length - 1}");
            oldToCompact[old] = i;
        }

        var fullMaps = new uint[source.FullMaps.Length][];
        var scatterMaps = new uint[source.ScatterMaps.Length][];
        for (int pi = 0; pi < source.FullMaps.Length; pi++)
        {
            fullMaps[pi] = new uint[source.FullMaps[pi].Length];
            scatterMaps[pi] = new uint[source.ScatterMaps[pi].Length];
            for (int li = 0; li < source.FullMaps[pi].Length; li++)
            {
                int compact = oldToCompact[source.FullMaps[pi][li]];
                fullMaps[pi][li] = compact >= 0 ? (uint)compact : Sentinel;
                scatterMaps[pi][li] = source.ScatterMaps[pi][li] != Sentinel && compact >= 0
                    ? (uint)compact : Sentinel;
            }
        }

        var union = new UnionResult(rows.Select(i => source.UnionHashes[i]).ToArray(), fullMaps,
            scatterMaps, rows.Select(i => source.Owner[i]).ToArray());
        return new UnionCompaction(union, rows, oldToCompact);
    }

    /// <summary>
    /// Ownership with the pipeline's own anchor preferred: every union bone the anchor's operator recovers
    /// SOUNDLY (its conditioning verdict, not its weight) moves to the anchor; the summed-weight argmax
    /// stands only for bones the anchor cannot constrain. A row recovered at the anchor's own draw is fresh
    /// exactly when the replacement is on screen, while another part's segment is only as fresh as that
    /// part's last draw — and scene states exist that render a pool source not at all (measured: zoomed
    /// dorm interactions issue zero draws for off-screen parts, shadow passes included), leaving its owned
    /// rows garbage until it first appears. Weight said the other part knows the bone better; the gate says
    /// the anchor knows it well enough, and the anchor is the one part guaranteed on screen.
    /// <para>Scatter maps are rebuilt from the adjusted owner. Union hashes and full maps are unchanged —
    /// ownership never moves a bone's union slot, so compiled donor indices are unaffected.</para>
    /// </summary>
    /// <param name="anchorWeak">The anchor operator's per-local-bone conditioning verdict
    /// (<c>OperatorArt.Weak</c>), indexed like the anchor's <see cref="UnionInput.Hashes"/>. A weak bone —
    /// including one the anchor's operator rescued with its own internal rigid tie — is NOT taken: the tie
    /// rides a co-bone, and a part posing the bone soundly live beats a rigid stand-in.</param>
    public static UnionResult PreferAnchorOwnership(UnionResult union, int anchorIdx, bool[] anchorWeak)
    {
        var anchorMap = union.FullMaps[anchorIdx];
        if (anchorWeak.Length != anchorMap.Length)
            throw new ArgumentException(
                $"anchor conditioning verdict covers {anchorWeak.Length} bones but the anchor maps {anchorMap.Length}");
        var owner = (int[])union.Owner.Clone();
        for (int li = 0; li < anchorMap.Length; li++)
            if (!anchorWeak[li]) owner[anchorMap[li]] = anchorIdx;

        var scatterMaps = new uint[union.FullMaps.Length][];
        for (int pi = 0; pi < union.FullMaps.Length; pi++)
        {
            var fm = union.FullMaps[pi];
            var sm = new uint[fm.Length];
            for (int li = 0; li < fm.Length; li++)
                sm[li] = owner[fm[li]] == pi ? fm[li] : Sentinel;
            scatterMaps[pi] = sm;
        }
        return union with { ScatterMaps = scatterMaps, Owner = owner };
    }

    /// <summary>
    /// Build the union bone order across the pool (first-seen in part order) plus the per-part full and
    /// scatter maps and per-bone ownership. A repeated bone hash must carry a byte-consistent bindpose
    /// (asserted within 1e-5) since the union keeps one bindpose per bone — parts authored in bind spaces
    /// one rigid rotation apart are restated in the anchor's space before they get here (see
    /// <see cref="Mesh.BindSpace"/>), so what this refuses is a delta that is no rigid space difference at
    /// all. Ownership = the pool part with the most summed weight on the bone (order-independent) — the
    /// same per-bone quantity <see cref="StreamDump.WeightedBoneHashes"/> reads off a bundle's mesh field
    /// and <see cref="MigotoEmitter.SummedWeights"/> off a dumped skin stream. The emitter then layers
    /// <see cref="PreferAnchorOwnership"/> on top once the anchor's conditioning verdict exists. This
    /// first-seen ordering is the single
    /// union-order authority — the emitted indices line up with any consumer given the SAME parts in the
    /// SAME order (it reproduces <see cref="SwapCompile.BuildUnionOrder"/>).
    /// </summary>
    public static UnionResult BuildUnion(IReadOnlyList<UnionInput> parts)
    {
        var unionIndex = new Dictionary<uint, int>();
        var unionHashes = new List<uint>();
        var bindOf = new Dictionary<uint, double[]>();
        var fullMaps = new List<uint[]>();

        foreach (var part in parts)
        {
            var hashes = part.Hashes;
            var fm = new uint[hashes.Length];
            for (int li = 0; li < hashes.Length; li++)
            {
                uint h = hashes[li];
                if (!unionIndex.TryGetValue(h, out int slot))
                {
                    slot = unionHashes.Count;
                    unionIndex[h] = slot;
                    unionHashes.Add(h);
                    bindOf[h] = part.Binds[h];
                }
                else
                {
                    var a = bindOf[h];
                    var b = part.Binds[h];
                    double d0 = 0.0;
                    for (int i = 0; i < a.Length; i++) d0 = Math.Max(d0, Math.Abs(a[i] - b[i]));
                    if (d0 > Mesh.BindSpace.MaxBindDisagreement)
                        throw new InvalidOperationException(
                            $"bone {h} has inconsistent bind poses across pool parts (max diff {d0:g4}); " +
                            "no measured or corroborated rigid rotation relates the two spaces, so the " +
                            "part can't be converted into the anchor's space");
                }
                fm[li] = (uint)unionIndex[h];
            }
            fullMaps.Add(fm);
        }

        int ub = unionHashes.Count;
        var support = new double[parts.Count, ub];
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var s2 = parts[pi].Stream2;
            var fm = fullMaps[pi];
            int verts = s2.Length / 32;
            for (int i = 0; i < verts; i++)
            {
                int o = i * 32;
                for (int k = 0; k < 4; k++)
                {
                    double wk = BitConverter.ToSingle(s2, o + k * 4);
                    if (wk > 0)
                    {
                        uint idx = BitConverter.ToUInt32(s2, o + 16 + k * 4);
                        support[pi, fm[idx]] += wk;
                    }
                }
            }
        }

        // owner = argmax over parts (first max wins on ties, matching numpy argmax)
        var owner = new int[ub];
        for (int u = 0; u < ub; u++)
        {
            int best = 0;
            double bestVal = support[0, u];
            for (int pi = 1; pi < parts.Count; pi++)
                if (support[pi, u] > bestVal) { bestVal = support[pi, u]; best = pi; }
            owner[u] = best;
        }

        var scatterMaps = new uint[parts.Count][];
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var fm = fullMaps[pi];
            var sm = new uint[fm.Length];
            for (int li = 0; li < fm.Length; li++)
                sm[li] = owner[fm[li]] == pi ? fm[li] : Sentinel;
            scatterMaps[pi] = sm;
        }

        return new UnionResult(unionHashes.ToArray(), fullMaps.ToArray(), scatterMaps, owner);
    }

    // ---- identity body: concatenate the pool parts, remapped to union bone order ----------------

    /// <summary>One part's raw streams for the identity concat.</summary>
    public sealed record IdentityPart(byte[] Stream0, byte[] Stream1, byte[] Stream2, byte[] Ib);

    /// <summary>Submesh record (byte offsets into the combined index buffer).</summary>
    public readonly record struct Submesh(int FirstByte, int IndexCount, int BaseVertex);

    /// <summary>Concatenated identity body: the four combined buffers plus the meta the mod consumes.</summary>
    public sealed record IdentityBodyResult(
        byte[] Bind, byte[] Vb1, byte[] Skin, byte[] Ib,
        int Verts, int Vb1Stride, IReadOnlyList<Submesh> Submeshes);

    /// <summary>
    /// Concatenate the pool parts into ONE mesh weighted to the union bone order (the character's own
    /// body driven through the pooled pipeline). stream2 bone indices are remapped local→union via
    /// <paramref name="maps"/> (the FULL map), the index buffer is offset by the running vertex count,
    /// and submeshes are recorded per source part. u16 only — a concat exceeding 65535 verts hard-fails.
    /// </summary>
    public static IdentityBodyResult BuildIdentityBody(IReadOnlyList<IdentityPart> parts, IReadOnlyList<uint[]> maps)
    {
        int total = parts.Sum(d => d.Stream0.Length / 40);
        if (total > 0xFFFF)
            throw new InvalidOperationException(
                $"identity concat has {total} verts — exceeds the u16 index range (65535); " +
                "widening the concat to R32 indices is not implemented");

        var bind = new List<byte>();
        var vb1 = new List<byte>();
        var skin = new List<byte>();
        var ib = new List<byte>();
        var submeshes = new List<Submesh>();
        int vbase = 0;
        int? vb1Stride = null;

        for (int pidx = 0; pidx < parts.Count; pidx++)
        {
            var d = parts[pidx];
            var m = maps[pidx];
            int n = d.Stream0.Length / 40;
            int st1 = d.Stream1.Length / n;
            if (vb1Stride is null) vb1Stride = st1;
            else if (vb1Stride != st1)
                throw new InvalidOperationException(
                    $"pool parts disagree on stream1 stride ({vb1Stride} vs {st1}); the reused vertex " +
                    "shader fetches vb1 at one stride — identity concat needs them equal");

            bind.AddRange(d.Stream0);
            vb1.AddRange(d.Stream1);

            var s2 = d.Stream2;
            for (int i = 0; i < n; i++)
            {
                int o = i * 32;
                for (int k = 0; k < 16; k++) skin.Add(s2[o + k]);          // weights verbatim
                for (int k = 0; k < 4; k++)
                {
                    uint idx = BitConverter.ToUInt32(s2, o + 16 + k * 4);
                    uint mapped = m[idx];
                    skin.Add((byte)(mapped & 0xFF));
                    skin.Add((byte)((mapped >> 8) & 0xFF));
                    skin.Add((byte)((mapped >> 16) & 0xFF));
                    skin.Add((byte)((mapped >> 24) & 0xFF));
                }
            }

            var pib = d.Ib;
            int cnt = pib.Length / 2;
            int firstByte = ib.Count;
            for (int i = 0; i < cnt; i++)
            {
                ushort val = (ushort)(BitConverter.ToUInt16(pib, i * 2) + vbase);
                ib.Add((byte)(val & 0xFF));
                ib.Add((byte)((val >> 8) & 0xFF));
            }
            submeshes.Add(new Submesh(firstByte, cnt, 0));
            vbase += n;
        }

        return new IdentityBodyResult(bind.ToArray(), vb1.ToArray(), skin.ToArray(), ib.ToArray(),
            vbase, vb1Stride ?? 20, submeshes);
    }
}
