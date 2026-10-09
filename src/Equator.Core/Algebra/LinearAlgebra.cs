namespace Equator.Core.Algebra;

/// <summary>Exact (symbolic) linear algebra on matrices represented as lists of row lists.</summary>
public static class LinearAlgebra
{
    public static bool IsMatrix(Expr e) => e is ListExpr l && l.IsMatrix;
    public static bool IsVector(Expr e) => e is ListExpr l && l.Items.Count > 0 && l.Items.All(i => i is not ListExpr);

    public static Expr[,] ToArray(Expr e)
    {
        if (!IsMatrix(e)) throw new MathException(Messages.T("Expected a matrix, e.g. [[1, 2], [3, 4]]."));
        var rows = ((ListExpr)e).Items.Cast<ListExpr>().ToList();
        var m = new Expr[rows.Count, rows[0].Items.Count];
        for (int i = 0; i < rows.Count; i++)
            for (int j = 0; j < rows[0].Items.Count; j++)
                m[i, j] = rows[i].Items[j];
        return m;
    }

    public static Expr FromArray(Expr[,] m) =>
        Expr.List(Enumerable.Range(0, m.GetLength(0)).Select(i => Expr.List(Enumerable.Range(0, m.GetLength(1)).Select(j => m[i, j]))));

    /// <summary>Normalises an entry: combines fractions and cancels common factors.</summary>
    public static Expr Simp(Expr e)
    {
        if (e.IsNumber) return e;
        if (e is Real r) return Math.Abs(r.Value) < 1e-13 ? Expr.Zero : e;
        var c = Rational.Cancel(e);
        return c.Complexity() <= e.Complexity() ? c : e;
    }

    private static bool IsZeroEntry(Expr e) => e.IsZero || (e is Real r && Math.Abs(r.Value) < 1e-12) || Simp(e).IsZero;

    public static Expr Identity(int n)
    {
        var m = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = i == j ? Expr.One : Expr.Zero;
        return FromArray(m);
    }

    public static Expr Transpose(Expr e)
    {
        if (IsVector(e)) return Expr.List(((ListExpr)e).Items.Select(x => Expr.List(x)));
        var m = ToArray(e);
        var t = new Expr[m.GetLength(1), m.GetLength(0)];
        for (int i = 0; i < m.GetLength(0); i++)
            for (int j = 0; j < m.GetLength(1); j++)
                t[j, i] = m[i, j];
        return FromArray(t);
    }

    /// <summary>Matrix/vector product with MATLAB-like shape rules (vector·vector is the dot product).</summary>
    public static Expr Dot(Expr a, Expr b)
    {
        if (IsVector(a) && IsVector(b))
        {
            var x = ((ListExpr)a).Items; var y = ((ListExpr)b).Items;
            if (x.Count != y.Count) throw new MathException(Messages.T("Dot product needs vectors of equal length ({0} vs {1}).", x.Count, y.Count));
            return Expr.Plus(x.Zip(y, (p, q) => p * q));
        }
        if (IsMatrix(a) && IsVector(b))
        {
            var m = ToArray(a); var v = ((ListExpr)b).Items;
            if (m.GetLength(1) != v.Count) throw new MathException(Messages.T("Incompatible shapes: {0}×{1} matrix times vector of length {2}.", m.GetLength(0), m.GetLength(1), v.Count));
            return Expr.List(Enumerable.Range(0, m.GetLength(0)).Select(i => Expr.Plus(Enumerable.Range(0, v.Count).Select(k => m[i, k] * v[k]))));
        }
        if (IsVector(a) && IsMatrix(b))
        {
            var v = ((ListExpr)a).Items; var m = ToArray(b);
            if (m.GetLength(0) != v.Count) throw new MathException(Messages.T("Incompatible shapes for vector times matrix."));
            return Expr.List(Enumerable.Range(0, m.GetLength(1)).Select(j => Expr.Plus(Enumerable.Range(0, v.Count).Select(k => v[k] * m[k, j]))));
        }
        var A = ToArray(a); var B = ToArray(b);
        if (A.GetLength(1) != B.GetLength(0))
            throw new MathException(Messages.T("Incompatible shapes: {0}×{1} times {2}×{3}.", A.GetLength(0), A.GetLength(1), B.GetLength(0), B.GetLength(1)));
        var C = new Expr[A.GetLength(0), B.GetLength(1)];
        for (int i = 0; i < A.GetLength(0); i++)
            for (int j = 0; j < B.GetLength(1); j++)
                C[i, j] = Expr.Plus(Enumerable.Range(0, A.GetLength(1)).Select(k => A[i, k] * B[k, j]));
        return FromArray(C);
    }

    public static Expr MatrixPower(Expr a, int n)
    {
        var m = ToArray(a);
        if (m.GetLength(0) != m.GetLength(1)) throw new MathException(Messages.T("Matrix power requires a square matrix."));
        if (n < 0) return MatrixPower(Inverse(a), -n);
        Expr result = Identity(m.GetLength(0));
        Expr square = a;
        while (n > 0)
        {
            if ((n & 1) == 1) result = MapEntries(Dot(result, square), Simp);
            n >>= 1;
            if (n > 0) square = MapEntries(Dot(square, square), Simp);
        }
        return result;
    }

    public static Expr MapEntries(Expr m, Func<Expr, Expr> f) =>
        m is ListExpr l ? Expr.List(l.Items.Select(i => MapEntries(i, f))) : f(m);

    public static Expr Determinant(Expr e)
    {
        var m = ToArray(e);
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new MathException(Messages.T("Determinant requires a square matrix."));
        bool numeric = m.Cast<Expr>().All(x => x.IsNumber);
        if (!numeric && n <= 4) return Simp(Expansion.Expand(Cofactor(m)));
        return Simp(Bareiss(m));
    }

    private static Expr Cofactor(Expr[,] m)
    {
        int n = m.GetLength(0);
        if (n == 1) return m[0, 0];
        if (n == 2) return m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
        var terms = new List<Expr>();
        for (int j = 0; j < n; j++)
        {
            if (m[0, j].IsZero) continue;
            var minor = new Expr[n - 1, n - 1];
            for (int r = 1; r < n; r++)
                for (int c = 0, cc = 0; c < n; c++)
                {
                    if (c == j) continue;
                    minor[r - 1, cc++] = m[r, c];
                }
            var t = m[0, j] * Cofactor(minor);
            terms.Add(j % 2 == 0 ? t : -t);
        }
        return Expr.Plus(terms);
    }

    /// <summary>Fraction-free elimination (exact division at each step).</summary>
    private static Expr Bareiss(Expr[,] src)
    {
        int n = src.GetLength(0);
        var m = (Expr[,])src.Clone();
        int sign = 1;
        Expr prev = Expr.One;
        for (int k = 0; k < n - 1; k++)
        {
            if (IsZeroEntry(m[k, k]))
            {
                int swap = -1;
                for (int i = k + 1; i < n; i++) if (!IsZeroEntry(m[i, k])) { swap = i; break; }
                if (swap < 0) return Expr.Zero;
                for (int j = 0; j < n; j++) (m[k, j], m[swap, j]) = (m[swap, j], m[k, j]);
                sign = -sign;
            }
            for (int i = k + 1; i < n; i++)
                for (int j = k + 1; j < n; j++)
                    m[i, j] = Simp((m[i, j] * m[k, k] - m[i, k] * m[k, j]) / prev);
            prev = m[k, k];
        }
        return sign > 0 ? m[n - 1, n - 1] : -m[n - 1, n - 1];
    }

    /// <summary>Reduced row echelon form; returns the pivot columns as well.</summary>
    public static (Expr[,] Matrix, List<int> Pivots) Rref(Expr[,] src)
    {
        var m = (Expr[,])src.Clone();
        int rows = m.GetLength(0), cols = m.GetLength(1);
        var pivots = new List<int>();
        int r = 0;
        for (int c = 0; c < cols && r < rows; c++)
        {
            int p = -1;
            // Prefer a numeric non-zero pivot, else any non-zero entry.
            for (int i = r; i < rows; i++) if (!IsZeroEntry(m[i, c]) && m[i, c].IsNumber) { p = i; break; }
            if (p < 0) for (int i = r; i < rows; i++) if (!IsZeroEntry(m[i, c])) { p = i; break; }
            if (p < 0) continue;
            for (int j = 0; j < cols; j++) (m[r, j], m[p, j]) = (m[p, j], m[r, j]);
            var pv = m[r, c];
            for (int j = 0; j < cols; j++) m[r, j] = Simp(m[r, j] / pv);
            for (int i = 0; i < rows; i++)
            {
                if (i == r || IsZeroEntry(m[i, c])) continue;
                var f = m[i, c];
                for (int j = 0; j < cols; j++) m[i, j] = Simp(m[i, j] - f * m[r, j]);
            }
            pivots.Add(c);
            r++;
        }
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
                if (IsZeroEntry(m[i, j])) m[i, j] = Expr.Zero;
        return (m, pivots);
    }

    public static int Rank(Expr e) => Rref(ToArray(e)).Pivots.Count;

    public static Expr Inverse(Expr e)
    {
        var m = ToArray(e);
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new MathException(Messages.T("Only square matrices can be inverted."));
        if (n <= 4 && !m.Cast<Expr>().All(x => x.IsNumber)) return AdjugateInverse(m);
        var aug = new Expr[n, 2 * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                aug[i, j] = m[i, j];
                aug[i, n + j] = i == j ? Expr.One : Expr.Zero;
            }
        var (r, pivots) = Rref(aug);
        if (pivots.Count < n || pivots[n - 1] >= n) throw new MathException(Messages.T("The matrix is singular (not invertible)."));
        var inv = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                inv[i, j] = r[i, n + j];
        return FromArray(inv);
    }

    private static Expr AdjugateInverse(Expr[,] m)
    {
        int n = m.GetLength(0);
        var det = Simp(Expansion.Expand(Cofactor(m)));
        if (det.IsZero) throw new MathException(Messages.T("The matrix is singular (not invertible)."));
        var inv = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                // inv[i, j] = cofactor C[j, i] / det
                var minor = new Expr[Math.Max(n - 1, 1), Math.Max(n - 1, 1)];
                for (int r = 0, rr = 0; r < n; r++)
                {
                    if (r == j) continue;
                    for (int c = 0, cc = 0; c < n; c++)
                    {
                        if (c == i) continue;
                        minor[rr, cc++] = m[r, c];
                    }
                    rr++;
                }
                var cof = n == 1 ? Expr.One : Expansion.Expand(Cofactor(minor));
                if ((i + j) % 2 == 1) cof = -cof;
                inv[i, j] = Simp(cof / det);
            }
        return FromArray(inv);
    }

    /// <summary>Basis of the null space as a list of vectors.</summary>
    public static List<Expr> NullSpace(Expr[,] m)
    {
        var (r, pivots) = Rref(m);
        int cols = m.GetLength(1);
        var free = Enumerable.Range(0, cols).Where(c => !pivots.Contains(c)).ToList();
        var basis = new List<Expr>();
        foreach (var f in free)
        {
            var v = new Expr[cols];
            for (int j = 0; j < cols; j++) v[j] = Expr.Zero;
            v[f] = Expr.One;
            for (int i = 0; i < pivots.Count; i++) v[pivots[i]] = Simp(-r[i, f]);
            // Clear denominators for nicer vectors when entries are rational.
            if (v.All(x => x is Num))
            {
                var lcm = v.Cast<Num>().Aggregate(System.Numerics.BigInteger.One, (acc, x) =>
                    acc / System.Numerics.BigInteger.GreatestCommonDivisor(acc, x.Value.Den) * x.Value.Den);
                v = v.Select(x => x * new Num(lcm)).ToArray();
            }
            basis.Add(Expr.List(v));
        }
        return basis;
    }

    public static Expr CharacteristicPolynomial(Expr a, Expr lambda)
    {
        var m = ToArray(a);
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new MathException(Messages.T("Characteristic polynomial requires a square matrix."));
        var shifted = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                shifted[i, j] = i == j ? lambda - m[i, j] : -m[i, j];
        var det = n <= 5 ? Cofactor(shifted) : Bareiss(shifted);
        return Expansion.Expand(det);
    }

    /// <summary>Eigenvalues with multiplicity as a list (via roots of the characteristic polynomial).</summary>
    public static List<Expr> Eigenvalues(Expr a)
    {
        var lambda = new Sym("\u0001lambda");
        var cp = CharacteristicPolynomial(a, lambda);
        var roots = Solver.PolynomialRoots(cp, lambda, withMultiplicity: true);
        if (roots is null) throw new MathException(Messages.T("Could not compute the eigenvalues."));
        return roots;
    }

    /// <summary>Pairs of [eigenvalue, multiplicity, basis vectors].</summary>
    public static Expr Eigenvectors(Expr a)
    {
        var m = ToArray(a);
        int n = m.GetLength(0);
        var values = Eigenvalues(a);
        var result = new List<Expr>();
        foreach (var group in values.GroupBy(v => v))
        {
            var lam = group.Key;
            var shifted = new Expr[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    shifted[i, j] = i == j ? m[i, j] - lam : m[i, j];
            var vecs = lam.ContainsAny(x => x is Real) ? NumericNullSpace(shifted) : NullSpace(shifted);
            result.Add(Expr.List(lam, new Num(group.Count()), Expr.List(vecs)));
        }
        return Expr.List(result);
    }

    private static List<Expr> NumericNullSpace(Expr[,] m)
    {
        // Treat tiny entries as zero so floating eigenvalues still yield a null space.
        int rows = m.GetLength(0), cols = m.GetLength(1);
        var d = new Expr[rows, cols];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
                d[i, j] = Numerics.NumericEval.FromComplex(Numerics.NumericEval.Eval(m[i, j]));
        var basis = NullSpace(d);
        return basis;
    }

    /// <summary>Solves A x = b for a (possibly non-square) system; throws when inconsistent.</summary>
    public static Expr LinSolve(Expr a, Expr b)
    {
        var m = ToArray(a);
        int rows = m.GetLength(0), cols = m.GetLength(1);
        var rhs = ((ListExpr)b).Items;
        if (rhs.Count != rows) throw new MathException(Messages.T("Right-hand side length does not match the number of rows."));
        var aug = new Expr[rows, cols + 1];
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++) aug[i, j] = m[i, j];
            aug[i, cols] = rhs[i];
        }
        var (r, pivots) = Rref(aug);
        if (pivots.Contains(cols)) throw new MathException(Messages.T("The linear system is inconsistent (no solution)."));
        var x = new Expr[cols];
        for (int j = 0; j < cols; j++) x[j] = Expr.Zero;
        for (int i = 0; i < pivots.Count; i++) x[pivots[i]] = r[i, cols];
        return Expr.List(x);
    }

    /// <summary>Gaussian elimination over the rationals for an n×(n+1) augmented matrix.</summary>
    public static BigRational[]? SolveRational(BigRational[,] aug, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int p = -1;
            for (int i = c; i < n; i++) if (!aug[i, c].IsZero) { p = i; break; }
            if (p < 0) return null;
            if (p != c)
                for (int j = 0; j <= n; j++) (aug[c, j], aug[p, j]) = (aug[p, j], aug[c, j]);
            var pv = aug[c, c];
            for (int j = c; j <= n; j++) aug[c, j] /= pv;
            for (int i = 0; i < n; i++)
            {
                if (i == c || aug[i, c].IsZero) continue;
                var f = aug[i, c];
                for (int j = c; j <= n; j++) aug[i, j] -= f * aug[c, j];
            }
        }
        var x = new BigRational[n];
        for (int i = 0; i < n; i++) x[i] = aug[i, n];
        return x;
    }

    public static Expr Cross(Expr a, Expr b)
    {
        if (a is not ListExpr { Items.Count: 3 } u || b is not ListExpr { Items.Count: 3 } v)
            throw new MathException(Messages.T("cross() needs two 3-vectors."));
        var x = u.Items; var y = v.Items;
        return Expr.List(x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]);
    }
}
