using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Synthetic numeric proof of the pooled-swap core, no game inputs: author a known LBS pose, recover the
/// bone palette, assert the reproduction residual is at the operator floor. Also proves union/owner/scatter
/// reconciliation and the identity-body concat on a hand-built two-part pool.
///
/// <para>The shipped operator is float32 (its recover shader reads float32), so the round-trip floor is
/// ~1e-6, not the ~1e-8 a float64 least-squares solve reaches.</para>
/// </summary>
public class PoolMathTests
{
    // ---- recover round-trip: BuildC + PInv invert linear-blend skinning -----------------------------

    [Fact]
    public void PInv_RecoversAuthoredPalette_ToFloat32Floor()
    {
        const int n = 60, nb = 4;
        var rng = new Random(1234);

        // bind positions and a fully-supported weight/index set (each bone gets real weight)
        var p = new double[n, 3];
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++)
        {
            for (int c = 0; c < 3; c++) p[v, c] = rng.NextDouble() * 2 - 1;
            int b0 = v % nb, b1 = (v + 1) % nb;
            double a = 0.3 + 0.4 * rng.NextDouble();
            w[v, 0] = a; w[v, 1] = 1 - a; w[v, 2] = 0; w[v, 3] = 0;
            bi[v, 0] = b0; bi[v, 1] = b1; bi[v, 2] = 0; bi[v, 3] = 0;
        }

        // arbitrary affines — LBS is linear, so no orthonormality is needed
        var pal = new double[nb][,];
        for (int b = 0; b < nb; b++)
        {
            var M = Identity4();
            M[0, 0] = 1 + 0.1 * (b + 1); M[1, 0] = 0.05 * b; M[2, 1] = -0.03 * (b + 1);
            M[3, 0] = 0.2 * b; M[3, 1] = -0.1 * (b + 1); M[3, 2] = 0.15 * b;   // translation row
            pal[b] = M;
        }

        var q = ApplyPalette(p, w, bi, pal);

        // recover through the shipped operator: a[:,j] = pinv @ q[:,j]
        var C = PoolMath.BuildC(p, w, bi, nb);
        var pinv = PoolMath.PInv(C, 1e-8);      // row-major (4*nb) x n float32
        int m = 4 * nb;
        var a2 = new double[m, 3];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < 3; j++)
            {
                double acc = 0;
                for (int col = 0; col < n; col++) acc += pinv[i * n + col] * q[col, j];
                a2[i, j] = acc;
            }
        var palHat = new double[nb][,];
        for (int b = 0; b < nb; b++)
        {
            var M = new double[4, 4];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 3; c++) M[r, c] = a2[4 * b + r, c];
            M[3, 3] = 1;
            palHat[b] = M;
        }

        var repro = ApplyPalette(p, w, bi, palHat);
        double maxResidual = 0;
        for (int v = 0; v < n; v++)
        {
            double dx = repro[v, 0] - q[v, 0], dy = repro[v, 1] - q[v, 1], dz = repro[v, 2] - q[v, 2];
            maxResidual = Math.Max(maxResidual, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        Assert.True(maxResidual < 1e-4, $"recover reproduction residual {maxResidual:e3} exceeds the float32 floor");
    }

    [Fact]
    public void PInv_ResultIsRowMajorAndCorrectSize()
    {
        var p = new double[3, 3] { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 } };
        var w = new double[3, 4] { { 1, 0, 0, 0 }, { 1, 0, 0, 0 }, { 1, 0, 0, 0 } };
        var bi = new int[3, 4];
        var C = PoolMath.BuildC(p, w, bi, 1);   // 1 bone -> C is 3 x 4
        var pinv = PoolMath.PInv(C);
        Assert.Equal(4 * 3, pinv.Length);       // (4*nbones) x nverts, row-major
    }

    // ---- slim anchor selection + the local left-inverse solve ---------------------------------------

    /// <summary>A deterministic non-coplanar cloud: any four points span the affine 3-space the LBS system
    /// needs, so a selection is rank-4 whenever it holds four.</summary>
    private static double[,] Cloud(int n)
    {
        var p = new double[n, 3];
        for (int v = 0; v < n; v++)
        {
            p[v, 0] = Math.Sin(1.7 * v + 0.3);
            p[v, 1] = Math.Cos(2.3 * v + 1.1);
            p[v, 2] = Math.Sin(0.9 * v) * Math.Cos(0.4 * v + 2);
        }
        return p;
    }

    /// <summary>The four left-inverse rows of a SINGLE-bone system, built INDEPENDENTLY of
    /// <see cref="PoolMath.PInv"/> — normal equations by Gauss-Jordan. Different arithmetic, same unique
    /// answer for a full-rank overdetermined system, which is what makes it a check.</summary>
    private static double[][] NormalEquationRows(double[,] p, double[] weight, int[] sel)
    {
        int m = sel.Length;
        var C = new double[m, 4];
        for (int i = 0; i < m; i++)
        {
            int v = sel[i];
            C[i, 0] = weight[v] * p[v, 0];
            C[i, 1] = weight[v] * p[v, 1];
            C[i, 2] = weight[v] * p[v, 2];
            C[i, 3] = weight[v];
        }
        var a = new double[4, 8];                      // [ CᵀC | I ]
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
                for (int i = 0; i < m; i++) a[r, c] += C[i, r] * C[i, c];
            a[r, 4 + r] = 1;
        }
        for (int c = 0; c < 4; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            for (int j = 0; j < 8; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
            double d = a[c, c];
            for (int j = 0; j < 8; j++) a[c, j] /= d;
            for (int r = 0; r < 4; r++)
            {
                if (r == c) continue;
                double f = a[r, c];
                for (int j = 0; j < 8; j++) a[r, j] -= f * a[c, j];
            }
        }
        var rows = new double[4][];
        for (int r = 0; r < 4; r++)
        {
            rows[r] = new double[m];
            for (int i = 0; i < m; i++)
                for (int c = 0; c < 4; c++) rows[r][i] += a[r, 4 + c] * C[i, c];
        }
        return rows;
    }

    [Fact]
    public void LocalPInvRows_MatchesAnIndependentLeftInverse_AndReportsZeroDefect()
    {
        const int n = 9;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        var weight = new double[n];
        for (int v = 0; v < n; v++) { w[v, 0] = weight[v] = 0.4 + 0.05 * v; }
        var sel = new int[n];
        for (int v = 0; v < n; v++) sel[v] = v;

        var (rows, dev) = PoolMath.LocalPInvRows(p, w, bi, bone: 0, sel, nbones: 1);

        var expected = NormalEquationRows(p, weight, sel);
        for (int r = 0; r < 4; r++)
            for (int i = 0; i < n; i++)
                Assert.Equal(expected[r][i], rows[r][i], 6);   // the normal-equation route squares the
                                                              // condition number: 1e-6 is ITS floor
        // a true left inverse over the selection: exact recovery for ANY palette, not just a probe
        Assert.True(dev < 1e-6, $"defect {dev} on a full-rank single-bone selection");
    }

    [Fact]
    public void LocalPInvRows_ReportsTheDefectWhenTheSelectionCannotDetermineTheBone()
    {
        // Three vertices cannot pin four unknowns: the min-norm rows are not a left inverse, and the defect
        // must say so rather than report the (zero) residual of the solve it did do.
        var p = Cloud(3);
        var w = new double[3, 4];
        var bi = new int[3, 4];
        for (int v = 0; v < 3; v++) w[v, 0] = 1;

        var (_, dev) = PoolMath.LocalPInvRows(p, w, bi, bone: 0, new[] { 0, 1, 2 }, nbones: 1);

        Assert.True(dev > 0.1, $"an underdetermined selection reported a defect of only {dev}");
    }

    [Fact]
    public void LocalPInvRows_NoSupportInTheSelection_IsInfinite()
    {
        var p = Cloud(4);
        var w = new double[4, 4];
        var bi = new int[4, 4];
        for (int v = 0; v < 4; v++) w[v, 0] = 1;      // everything on bone 0

        var (rows, dev) = PoolMath.LocalPInvRows(p, w, bi, bone: 1, new[] { 0, 1, 2, 3 }, nbones: 2);

        Assert.Equal(double.PositiveInfinity, dev);
        Assert.All(rows, r => Assert.All(r, c => Assert.Equal(0, c)));
    }

    [Fact]
    public void LocalPInvRows_DefectCountsTheColumnsTheSolveDropped()
    {
        // Every vertex is 50/50 on two bones, so their coefficient columns are identical. Capped at one
        // bone the solve's own residual is zero, but those rows recover the OTHER bone's palette just as
        // strongly. The defect spans every bone weighted in the selection, so it reports the full unit of
        // misattribution instead.
        const int n = 8;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++) { w[v, 0] = 0.5; w[v, 1] = 0.5; bi[v, 0] = 0; bi[v, 1] = 1; }
        var sel = new int[n];
        for (int v = 0; v < n; v++) sel[v] = v;

        var (rows, dev) = PoolMath.LocalPInvRows(p, w, bi, bone: 0, sel, nbones: 2, capDivisor: 8);

        // the bone-local view: the rows invert the target's own columns to working precision
        double local = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                double acc = 0;
                for (int i = 0; i < n; i++)
                    acc += (float)rows[r][i] * 0.5 * (c == 3 ? 1 : p[sel[i], c]);
                local = Math.Max(local, Math.Abs(acc - (c == r ? 1.0 : 0.0)));
            }
        Assert.True(local < 1e-4, $"the local solve itself did not converge (residual {local})");
        Assert.True(dev > 0.5, $"the dropped co-bone left the defect at {dev}");
    }

    [Fact]
    public void LocalPInvRows_IgnoresInfluencesOutsideThePalette()
    {
        // BuildC gives no column to a bone index outside the palette, so neither may the defect model — else
        // a malformed dump makes the two disagree about the system's shape.
        const int n = 8;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++)
        {
            w[v, 0] = 1; bi[v, 0] = 0;
            w[v, 1] = 0.5; bi[v, 1] = 4000;      // outside a 1-bone palette
            w[v, 2] = 0.5; bi[v, 2] = -3;
        }
        var sel = new int[n];
        for (int v = 0; v < n; v++) sel[v] = v;

        var (_, dev) = PoolMath.LocalPInvRows(p, w, bi, bone: 0, sel, nbones: 1);

        Assert.True(dev < 1e-6, $"out-of-palette influences reached the defect model (defect {dev})");
    }

    [Fact]
    public void LocalPInvRows_CountsNegativeWeightsAsBuildCDoes()
    {
        // BuildC skips an influence only when its weight is EXACTLY zero; a negative weight is a real
        // column. The defect model uses the same test, so dropping it shows up as misattribution.
        const int n = 8;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++) { w[v, 0] = 1; bi[v, 0] = 0; w[v, 1] = -0.3; bi[v, 1] = 1; }
        var sel = new int[n];
        for (int v = 0; v < n; v++) sel[v] = v;

        var (_, dev) = PoolMath.LocalPInvRows(p, w, bi, bone: 0, sel, nbones: 2, capDivisor: 8);

        Assert.True(dev > 0.2, $"the negative-weight bone never reached the defect model (defect {dev})");
    }

    [Fact]
    public void LocalPInvRows_IsDeterministicAcrossCallsAndCapLevels()
    {
        // The emitted bytes are a contract and the caller picks a level by comparing defects: identical
        // inputs give bit-identical rows, and each level its own answer.
        const int n = 12;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++) { w[v, 0] = 0.7; bi[v, 0] = 0; w[v, 1] = 0.3; bi[v, 1] = 1 + v % 2; }
        var sel = new int[n];
        for (int v = 0; v < n; v++) sel[v] = v;

        var (a, da) = PoolMath.LocalPInvRows(p, w, bi, 0, sel, nbones: 3, capDivisor: 4);
        var (b, db) = PoolMath.LocalPInvRows(p, w, bi, 0, sel, nbones: 3, capDivisor: 4);
        Assert.Equal(da, db);
        for (int r = 0; r < 4; r++) Assert.Equal(a[r], b[r]);
    }

    [Fact]
    public void SelectAnchorRows_TakesOnlyTheBonesOwnVertices_Ascending_AndSpreads()
    {
        // Ten candidates clustered at one position plus one far vertex at a LOWER weight: rank alone takes
        // the cluster and loses rank-4 support, so the farthest-point spread must reach the outlier.
        const int n = 12;
        var p = new double[n, 3];
        for (int v = 0; v < n; v++) { p[v, 0] = 0.001 * v; p[v, 1] = 0; p[v, 2] = 0; }
        p[7, 0] = 50;                                  // the outlier
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++) { w[v, 0] = v == 7 ? 0.2 : 0.9; bi[v, 0] = v < 10 || v == 7 ? 0 : 1; }

        var pick = PoolMath.SelectAnchorRows(p, w, bi, bone: 0, k: 3);

        Assert.Equal(3, pick.Length);
        Assert.Equal(pick.OrderBy(x => x).ToArray(), pick);
        Assert.Contains(7, pick);
        Assert.All(pick, v => Assert.True(v < 10, $"vertex {v} is not weighted to bone 0"));
        Assert.Equal(pick, PoolMath.SelectAnchorRows(p, w, bi, bone: 0, k: 3));
    }

    [Fact]
    public void SelectAnchorRows_CapsAtTheCandidateCount_AndIsEmptyWithoutSupport()
    {
        const int n = 6;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++) { w[v, 0] = 1; bi[v, 0] = v < 2 ? 0 : 1; }

        Assert.Equal(2, PoolMath.SelectAnchorRows(p, w, bi, bone: 0, k: 32).Length);
        Assert.Empty(PoolMath.SelectAnchorRows(p, w, bi, bone: 7, k: 32));
    }

    [Fact]
    public void SelectDiscriminatorRows_TakesCoboneVerticesTheTargetDoesNotTouch()
    {
        // vertices 0..3 carry bone 0 and bone 1 together (the selection); 4..7 carry bone 1 alone (the
        // discriminators); 8..9 carry bone 1 AND a trace of bone 0, which is not clean enough to pin with.
        const int n = 10;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < 4; v++) { w[v, 0] = 0.5; bi[v, 0] = 0; w[v, 1] = 0.5; bi[v, 1] = 1; }
        for (int v = 4; v < 8; v++) { w[v, 0] = 1.0; bi[v, 0] = 1; }
        for (int v = 8; v < 10; v++) { w[v, 0] = 0.999; bi[v, 0] = 1; w[v, 1] = 1e-3; bi[v, 1] = 0; }
        var sel = new[] { 0, 1, 2, 3 };

        var disc = PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: sel, nbones: 2, kd: 8);

        Assert.Equal(new[] { 4, 5, 6, 7 }, disc);       // ascending, and the trace-weighted pair is out
    }

    [Fact]
    public void SelectDiscriminatorRows_SpreadsItsBudgetAcrossCobones_Deterministically()
    {
        // two co-bones in the selection, 1 the heavier. A budget of 2 must reach BOTH — a budget spent on
        // the strongest co-bone alone leaves the other's columns as unpinned as before.
        const int n = 9;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < 3; v++)
        { w[v, 0] = 0.4; bi[v, 0] = 0; w[v, 1] = 0.5; bi[v, 1] = 1; w[v, 2] = 0.1; bi[v, 2] = 2; }
        // bone 1's clean vertices, 4 the strongest; bone 2's, 7 the strongest
        w[3, 0] = 0.6; bi[3, 0] = 1;
        w[4, 0] = 0.9; bi[4, 0] = 1;
        w[5, 0] = 0.9; bi[5, 0] = 1;                    // ties 4 on weight, loses on index
        w[6, 0] = 0.2; bi[6, 0] = 2;
        w[7, 0] = 0.8; bi[7, 0] = 2;
        w[8, 0] = 0.3; bi[8, 0] = 2;
        var sel = new[] { 0, 1, 2 };

        var two = PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: sel, nbones: 3, kd: 2);
        Assert.Equal(new[] { 4, 7 }, two);              // one per co-bone, each its best

        var four = PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: sel, nbones: 3, kd: 4);
        Assert.Equal(new[] { 4, 5, 7, 8 }, four);       // second round: the next best of each
        Assert.Equal(four, PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: sel, nbones: 3, kd: 4));
    }

    [Fact]
    public void SelectDiscriminatorRows_IsEmptyWhenNothingSeparates()
    {
        const int n = 6;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < n; v++) { w[v, 0] = 0.5; bi[v, 0] = 0; w[v, 1] = 0.5; bi[v, 1] = 1; }
        var sel = new[] { 0, 1, 2, 3, 4, 5 };

        // every vertex is already selected, so there is nothing left to pin the co-bone with
        Assert.Empty(PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: sel, nbones: 2, kd: 8));
        // and a selection with no co-bone at all has nothing to discriminate against
        var solo = new double[n, 4];
        var soloBi = new int[n, 4];
        for (int v = 0; v < n; v++) { solo[v, 0] = 1; soloBi[v, 0] = 0; }
        Assert.Empty(PoolMath.SelectDiscriminatorRows(p, solo, soloBi, bone: 0, alreadySelected: new[] { 0, 1 }, nbones: 1, kd: 8));
        Assert.Empty(PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: new[] { 0 }, nbones: 2, kd: 0));
    }

    [Fact]
    public void SelectDiscriminatorRows_DoesNotSpendWidthPinningOutOfPaletteCobones()
    {
        // The selection is co-weighted with bone 1 and with an index outside the palette. LocalPInvRows
        // gives the latter no column, so rows picked to pin it would widen the bone for nothing.
        const int n = 6;
        var p = Cloud(n);
        var w = new double[n, 4];
        var bi = new int[n, 4];
        for (int v = 0; v < 2; v++)
        { w[v, 0] = 0.4; bi[v, 0] = 0; w[v, 1] = 0.3; bi[v, 1] = 1; w[v, 2] = 0.3; bi[v, 2] = 4000; }
        for (int v = 2; v < 4; v++) { w[v, 0] = 1; bi[v, 0] = 1; }        // bone 1's clean vertices
        for (int v = 4; v < 6; v++) { w[v, 0] = 1; bi[v, 0] = 4000; }     // the out-of-palette bone's
        var sel = new[] { 0, 1 };

        var disc = PoolMath.SelectDiscriminatorRows(p, w, bi, bone: 0, alreadySelected: sel, nbones: 2, kd: 8);

        Assert.Equal(new[] { 2, 3 }, disc);
    }

    // ---- union / owner / scatter reconciliation -----------------------------------------------------

    [Fact]
    public void BuildUnion_FirstSeenOrder_OwnerByMaxSupport_ScatterSentinels()
    {
        // part0 bones [10,20,30]; part1 bones [20,40] (shares 20). first-seen union: [10,20,30,40].
        var bind = Bindpose();
        var part0 = new PoolMath.UnionInput(
            new uint[] { 10, 20, 30 },
            new Dictionary<uint, double[]> { [10] = bind, [20] = bind, [30] = bind },
            Stream2(new[] { (1.0, 0.0, 0.0, 0.0), (0.5, 0.5, 0.0, 0.0) },   // weights per vertex
                    new[] { (0, 0, 0, 0), (1, 2, 0, 0) }));                  // local bone indices
        var part1 = new PoolMath.UnionInput(
            new uint[] { 20, 40 },
            new Dictionary<uint, double[]> { [20] = bind, [40] = bind },
            Stream2(new[] { (0.3, 0.7, 0.0, 0.0) }, new[] { (0, 1, 0, 0) }));

        var u = PoolMath.BuildUnion(new[] { part0, part1 });

        Assert.Equal(new uint[] { 10, 20, 30, 40 }, u.UnionHashes);
        Assert.Equal(new uint[] { 0, 1, 2 }, u.FullMaps[0]);
        Assert.Equal(new uint[] { 1, 3 }, u.FullMaps[1]);
        // bone 20 (union slot 1): part0 support 0.5 > part1 support 0.3 -> part0 owns; 40 -> part1
        Assert.Equal(new[] { 0, 0, 0, 1 }, u.Owner);
        // part0 owns all three of its bones
        Assert.Equal(new uint[] { 0, 1, 2 }, u.ScatterMaps[0]);
        // part1 does NOT own 20 (sentinel) but owns 40 (slot 3)
        Assert.Equal(new[] { PoolMath.Sentinel, 3u }, u.ScatterMaps[1]);
    }

    [Fact]
    public void PreferAnchorOwnership_SoundAnchorBone_TakesTheRowFromTheWeightWinner()
    {
        // part0 bones [10,20,30]; part1 (the anchor) bones [20,40]. By weight part0 owns 20; the anchor's
        // operator recovers 20 soundly, so ownership moves — and 10/30, which the anchor never maps, stay.
        var bind = Bindpose();
        var part0 = new PoolMath.UnionInput(
            new uint[] { 10, 20, 30 },
            new Dictionary<uint, double[]> { [10] = bind, [20] = bind, [30] = bind },
            Stream2(new[] { (1.0, 0.0, 0.0, 0.0), (0.5, 0.5, 0.0, 0.0) },
                    new[] { (0, 0, 0, 0), (1, 2, 0, 0) }));
        var part1 = new PoolMath.UnionInput(
            new uint[] { 20, 40 },
            new Dictionary<uint, double[]> { [20] = bind, [40] = bind },
            Stream2(new[] { (0.3, 0.7, 0.0, 0.0) }, new[] { (0, 1, 0, 0) }));
        var u = PoolMath.BuildUnion(new[] { part0, part1 });
        Assert.Equal(new[] { 0, 0, 0, 1 }, u.Owner);   // the argmax baseline this preference adjusts

        var adjusted = PoolMath.PreferAnchorOwnership(u, anchorIdx: 1, anchorWeak: new[] { false, false });

        Assert.Equal(new[] { 0, 1, 0, 1 }, adjusted.Owner);
        // scatter maps follow: part0 loses 20 (sentinel), the anchor gains it
        Assert.Equal(new uint[] { 0, PoolMath.Sentinel, 2 }, adjusted.ScatterMaps[0]);
        Assert.Equal(new uint[] { 1, 3 }, adjusted.ScatterMaps[1]);
        // slots and maps never move — compiled donor indices ride them
        Assert.Equal(u.UnionHashes, adjusted.UnionHashes);
        Assert.Equal(u.FullMaps, adjusted.FullMaps);
    }

    [Fact]
    public void PreferAnchorOwnership_WeakAnchorBone_LeavesTheWeightWinnerOwning()
    {
        var bind = Bindpose();
        var part0 = new PoolMath.UnionInput(
            new uint[] { 10, 20 },
            new Dictionary<uint, double[]> { [10] = bind, [20] = bind },
            Stream2(new[] { (1.0, 0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0) },
                    new[] { (0, 0, 0, 0), (1, 0, 0, 0) }));
        var part1 = new PoolMath.UnionInput(
            new uint[] { 20 },
            new Dictionary<uint, double[]> { [20] = bind },
            Stream2(new[] { (0.4, 0.0, 0.0, 0.0) }, new[] { (0, 0, 0, 0) }));
        var u = PoolMath.BuildUnion(new[] { part0, part1 });

        // the anchor maps bone 20 but recovers it ill-conditioned — a weak verdict takes nothing, so the
        // whole result is the input's
        var adjusted = PoolMath.PreferAnchorOwnership(u, anchorIdx: 1, anchorWeak: new[] { true });

        Assert.Equal(new[] { 0, 0 }, adjusted.Owner);
        Assert.Equal(u.ScatterMaps, adjusted.ScatterMaps);
    }

    [Fact]
    public void PreferAnchorOwnership_VerdictBoneCountMismatch_Throws()
    {
        var bind = Bindpose();
        var part0 = new PoolMath.UnionInput(new uint[] { 10 },
            new Dictionary<uint, double[]> { [10] = bind },
            Stream2(new[] { (1.0, 0.0, 0.0, 0.0) }, new[] { (0, 0, 0, 0) }));
        var u = PoolMath.BuildUnion(new[] { part0 });

        Assert.Throws<ArgumentException>(() =>
            PoolMath.PreferAnchorOwnership(u, anchorIdx: 0, anchorWeak: new[] { false, false }));
    }

    [Fact]
    public void BuildUnion_InconsistentBindposeAcrossParts_Throws()
    {
        var b0 = Bindpose();
        var b1 = Bindpose(); b1[0] += 1.0;   // shared bone with a different bind pose
        var part0 = new PoolMath.UnionInput(new uint[] { 5 },
            new Dictionary<uint, double[]> { [5] = b0 }, Stream2(new[] { (1.0, 0.0, 0.0, 0.0) }, new[] { (0, 0, 0, 0) }));
        var part1 = new PoolMath.UnionInput(new uint[] { 5 },
            new Dictionary<uint, double[]> { [5] = b1 }, Stream2(new[] { (1.0, 0.0, 0.0, 0.0) }, new[] { (0, 0, 0, 0) }));
        Assert.Throws<InvalidOperationException>(() => PoolMath.BuildUnion(new[] { part0, part1 }));
    }

    // ---- identity-body concat -----------------------------------------------------------------------

    [Fact]
    public void BuildIdentityBody_ConcatsRemapsAndOffsets()
    {
        // two parts, 2 + 1 verts; full maps remap local bone indices to the union.
        var p0 = new PoolMath.IdentityPart(
            Stream0(2), Stream1(2), Stream2(new[] { (1.0, 0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0) }, new[] { (0, 0, 0, 0), (1, 0, 0, 0) }),
            Ib(new ushort[] { 0, 1, 0 }));
        var p1 = new PoolMath.IdentityPart(
            Stream0(1), Stream1(1), Stream2(new[] { (1.0, 0.0, 0.0, 0.0) }, new[] { (0, 0, 0, 0) }),
            Ib(new ushort[] { 0, 0, 0 }));
        var maps = new[] { new uint[] { 0, 1 }, new uint[] { 3 } };   // part0 bones -> {0,1}; part1 bone -> {3}

        var body = PoolMath.BuildIdentityBody(new[] { p0, p1 }, maps);

        Assert.Equal(3, body.Verts);
        Assert.Equal(20, body.Vb1Stride);
        Assert.Equal(2, body.Submeshes.Count);
        Assert.Equal(new PoolMath.Submesh(0, 3, 0), body.Submeshes[0]);
        Assert.Equal(new PoolMath.Submesh(6, 3, 0), body.Submeshes[1]);   // 3 idx * 2 bytes after part0

        // combined ib: part1's indices are offset by part0's vertex count (2)
        Assert.Equal(2, BitConverter.ToUInt16(body.Ib, 6));              // part1 index 0 -> 2

        // combined skin: part1 vertex bone index 0 remapped via full map to union slot 3
        int part1SkinOffset = 2 * 32 + 16;                              // after 2 part0 verts, into the index quad
        Assert.Equal(3u, BitConverter.ToUInt32(body.Skin, part1SkinOffset));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    static double[,] Identity4()
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++) m[i, i] = 1;
        return m;
    }

    // row-vector LBS: q_v = sum_k w * ([p,1] @ pal[b])[0:3]
    static double[,] ApplyPalette(double[,] p, double[,] w, int[,] bi, double[][,] pal)
    {
        int n = p.GetLength(0);
        var outp = new double[n, 3];
        for (int v = 0; v < n; v++)
        {
            double[] ph = { p[v, 0], p[v, 1], p[v, 2], 1.0 };
            for (int k = 0; k < 4; k++)
            {
                double wk = w[v, k];
                if (wk == 0) continue;
                var M = pal[bi[v, k]];
                for (int j = 0; j < 3; j++)
                {
                    double acc = 0;
                    for (int r = 0; r < 4; r++) acc += ph[r] * M[r, j];
                    outp[v, j] += wk * acc;
                }
            }
        }
        return outp;
    }

    static double[] Bindpose()
    {
        var b = new double[16];
        for (int i = 0; i < 16; i++) b[i] = (i % 5) * 0.25 - 0.5;
        return b;
    }

    static byte[] Stream2((double, double, double, double)[] weights, (int, int, int, int)[] indices)
    {
        var buf = new byte[weights.Length * 32];
        for (int i = 0; i < weights.Length; i++)
        {
            var (w0, w1, w2, w3) = weights[i];
            var (i0, i1, i2, i3) = indices[i];
            int o = i * 32;
            foreach (var (idx, val) in new[] { (0, w0), (1, w1), (2, w2), (3, w3) })
                BitConverter.GetBytes((float)val).CopyTo(buf, o + idx * 4);
            foreach (var (idx, val) in new[] { (0, i0), (1, i1), (2, i2), (3, i3) })
                BitConverter.GetBytes((uint)val).CopyTo(buf, o + 16 + idx * 4);
        }
        return buf;
    }

    static byte[] Stream0(int verts) => new byte[verts * 40];
    static byte[] Stream1(int verts) => new byte[verts * 20];

    static byte[] Ib(ushort[] idx)
    {
        var buf = new byte[idx.Length * 2];
        for (int i = 0; i < idx.Length; i++) BitConverter.GetBytes(idx[i]).CopyTo(buf, i * 2);
        return buf;
    }
}
