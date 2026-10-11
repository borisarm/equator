using Equator.Core.Calculus;

namespace Equator.Core.Algebra;

/// <summary>
/// Matrix factorisations and the subspaces, forms and functions built on them: LU, QR, Cholesky, singular values
/// and the SVD, Gram–Schmidt, column and row spaces, least squares and the pseudo-inverse, diagonalisation, the
/// Jordan form, the minimal polynomial, the matrix exponential and the adjugate. Everything is exact when the
/// entries are; matrices with floating-point entries fall back to numerical methods where exact ones cannot work.
/// </summary>
public static class Decompositions
{
    // ---- helpers ---------------------------------------------------------------------------------------

    private static int Rows(Expr[,] m) => m.GetLength(0);
    private static int Cols(Expr[,] m) => m.GetLength(1);

    private static bool HasReals(Expr[,] m) => m.Cast<Expr>().Any(x => x.ContainsAny(z => z is Real));

    /// <summary>Simplifies an entry that may contain radicals (QR, Cholesky, Gram–Schmidt produce them).</summary>
    private static Expr Clean(Expr e)
    {
        if (e.IsNumber) return e;
        if (e is Real r) return Math.Abs(r.Value) < 1e-13 ? Expr.Zero : e;
        return Simplifier.Simplify(e);
    }

    private static bool IsZero(Expr e) => e.IsZero || (e is Real r && Math.Abs(r.Value) < 1e-11) || Clean(e).IsZero;

    private static Expr[,] SquareArray(Expr a, string fn)
    {
        var m = LinearAlgebra.ToArray(a);
        if (Rows(m) != Cols(m)) throw new MathException(Messages.T("{0} requires a square matrix.", fn));
        return m;
    }

    private static Expr[,] Mul(Expr[,] a, Expr[,] b, Func<Expr, Expr> clean)
    {
        var c = new Expr[Rows(a), Cols(b)];
        for (int i = 0; i < Rows(a); i++)
            for (int j = 0; j < Cols(b); j++)
                c[i, j] = clean(Expansion.Expand(Expr.Plus(Enumerable.Range(0, Cols(a)).Select(k => a[i, k] * b[k, j]))));
        return c;
    }

    private static Expr[,] Transpose(Expr[,] m)
    {
        var t = new Expr[Cols(m), Rows(m)];
        for (int i = 0; i < Rows(m); i++)
            for (int j = 0; j < Cols(m); j++)
                t[j, i] = m[i, j];
        return t;
    }

    private static Expr[,] Identity(int n)
    {
        var m = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = i == j ? Expr.One : Expr.Zero;
        return m;
    }

    private static Expr[] Column(Expr[,] m, int j) => Enumerable.Range(0, Rows(m)).Select(i => m[i, j]).ToArray();

    private static Expr[,] FromColumns(IReadOnlyList<Expr[]> columns, int rows)
    {
        var m = new Expr[rows, columns.Count];
        for (int j = 0; j < columns.Count; j++)
            for (int i = 0; i < rows; i++)
                m[i, j] = columns[j][i];
        return m;
    }

    private static Expr[] Vector(Expr v) => ((ListExpr)v).Items.ToArray();

    private static Expr Dot(Expr[] u, Expr[] v) => Expr.Plus(u.Zip(v, (a, b) => a * b));

    private static Expr Matrix(Expr[,] m) => LinearAlgebra.FromArray(m);

    private static int Rank(IReadOnlyList<Expr[]> vectors)
    {
        if (vectors.Count == 0) return 0;
        var m = new Expr[vectors.Count, vectors[0].Length];
        for (int i = 0; i < vectors.Count; i++)
            for (int j = 0; j < vectors[0].Length; j++)
                m[i, j] = vectors[i][j];
        return LinearAlgebra.Rref(m).Pivots.Count;
    }

    // ---- LU ----------------------------------------------------------------------------------------------

    /// <summary>[L, U, P] with P·A = L·U: L unit lower triangular, U in row echelon form, P a permutation.</summary>
    public static Expr Lu(Expr a)
    {
        var u = (Expr[,])LinearAlgebra.ToArray(a).Clone();
        int m = Rows(u), n = Cols(u);
        var l = Identity(m);
        var perm = Enumerable.Range(0, m).ToArray();
        int r = 0;
        for (int c = 0; c < n && r < m; c++)
        {
            int p = -1;
            for (int i = r; i < m; i++) if (!IsZero(u[i, c])) { p = i; break; }
            if (p < 0) continue;
            if (p != r)
            {
                for (int j = 0; j < n; j++) (u[r, j], u[p, j]) = (u[p, j], u[r, j]);
                for (int j = 0; j < r; j++) (l[r, j], l[p, j]) = (l[p, j], l[r, j]);
                (perm[r], perm[p]) = (perm[p], perm[r]);
            }
            for (int i = r + 1; i < m; i++)
            {
                if (IsZero(u[i, c])) { u[i, c] = Expr.Zero; continue; }
                var f = LinearAlgebra.Simp(u[i, c] / u[r, c]);
                l[i, r] = f;
                for (int j = 0; j < n; j++) u[i, j] = j == c ? Expr.Zero : LinearAlgebra.Simp(u[i, j] - f * u[r, j]);
            }
            r++;
        }
        var pm = new Expr[m, m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                pm[i, j] = perm[i] == j ? Expr.One : Expr.Zero;
        return Expr.List(Matrix(l), Matrix(u), Matrix(pm));
    }

    // ---- Cholesky ----------------------------------------------------------------------------------------

    /// <summary>The lower triangular L with A = L·Lᵀ, for a symmetric positive definite A.</summary>
    public static Expr Cholesky(Expr a)
    {
        var m = SquareArray(a, "cholesky");
        int n = Rows(m);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (!IsZero(m[i, j] - m[j, i])) throw new MathException(Messages.T("cholesky: the matrix is not symmetric."));
        var l = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                l[i, j] = Expr.Zero;
        for (int j = 0; j < n; j++)
        {
            var d = Clean(Expansion.Expand(m[j, j] - Expr.Plus(Enumerable.Range(0, j).Select(k => Expr.Power(l[j, k], 2)))));
            if (d.IsConstant && !d.Contains(Expr.I))
            {
                var v = Numerics.NumericEval.EvalReal(d);
                if (!(v > 1e-12)) throw new MathException(Messages.T("cholesky: the matrix is not positive definite."));
            }
            l[j, j] = Clean(Expr.Sqrt(d));
            for (int i = j + 1; i < n; i++)
                l[i, j] = Clean(Expansion.Expand(m[i, j] - Expr.Plus(Enumerable.Range(0, j).Select(k => l[i, k] * l[j, k]))) / l[j, j]);
        }
        return Matrix(l);
    }

    // ---- Gram–Schmidt and QR -----------------------------------------------------------------------------

    /// <summary>
    /// Orthogonalises the vectors in order (classical Gram–Schmidt), dropping those that depend on the earlier
    /// ones; normalises them unless asked not to.
    /// </summary>
    public static List<Expr[]> GramSchmidt(IReadOnlyList<Expr[]> vectors, bool normalize)
    {
        var orth = new List<Expr[]>();
        foreach (var v in vectors)
        {
            var w = v.ToArray();
            foreach (var q in orth)
            {
                var c = Clean(Dot(q, v) / Dot(q, q));
                if (c.IsZero) continue;
                for (int i = 0; i < w.Length; i++) w[i] = Clean(Expansion.Expand(w[i] - c * q[i]));
            }
            if (w.All(IsZero)) continue;
            orth.Add(w.Select(x => IsZero(x) ? Expr.Zero : x).ToArray());
        }
        if (!normalize) return orth;
        return orth.Select(q =>
        {
            var norm = Clean(Expr.Sqrt(Clean(Expansion.Expand(Dot(q, q)))));
            return q.Select(x => Clean(x / norm)).ToArray();
        }).ToList();
    }

    /// <summary>[Q, R] with A = Q·R: Q with orthonormal columns, R upper triangular with a positive diagonal.</summary>
    public static Expr Qr(Expr a)
    {
        var m = LinearAlgebra.ToArray(a);
        int rows = Rows(m), cols = Cols(m);
        var columns = Enumerable.Range(0, cols).Select(j => Column(m, j)).ToList();
        var q = GramSchmidt(columns, normalize: true);
        if (q.Count < cols) throw new MathException(Messages.T("qr: the columns of the matrix must be linearly independent."));
        var r = new Expr[cols, cols];
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < cols; j++)
                r[i, j] = j < i ? Expr.Zero : Clean(Expansion.Expand(Dot(q[i], columns[j])));
        return Expr.List(Matrix(FromColumns(q, rows)), Matrix(r));
    }

    // ---- subspaces ---------------------------------------------------------------------------------------

    /// <summary>A basis of the column space: the pivot columns of A.</summary>
    public static List<Expr> ColumnSpace(Expr a)
    {
        var m = LinearAlgebra.ToArray(a);
        return LinearAlgebra.Rref(m).Pivots.Select(j => Expr.List(Column(m, j))).ToList();
    }

    /// <summary>A basis of the row space: the non-zero rows of the reduced row echelon form.</summary>
    public static List<Expr> RowSpace(Expr a)
    {
        var (r, pivots) = LinearAlgebra.Rref(LinearAlgebra.ToArray(a));
        return Enumerable.Range(0, pivots.Count).Select(i => Expr.List(Enumerable.Range(0, Cols(r)).Select(j => r[i, j]))).ToList();
    }

    /// <summary>The orthogonal projection of u onto v.</summary>
    public static Expr Projection(Expr u, Expr v)
    {
        if (!LinearAlgebra.IsVector(u) || !LinearAlgebra.IsVector(v)) throw new MathException(Messages.T("proj needs two vectors."));
        var x = Vector(u); var y = Vector(v);
        if (x.Length != y.Length) throw new MathException(Messages.T("Dot product needs vectors of equal length ({0} vs {1}).", x.Length, y.Length));
        var vv = Clean(Expansion.Expand(Dot(y, y)));
        if (vv.IsZero) throw new MathException(Messages.T("proj: cannot project onto the zero vector."));
        var c = Clean(Dot(x, y) / vv);
        return Expr.List(y.Select(t => Clean(c * t)));
    }

    // ---- pseudo-inverse and least squares ----------------------------------------------------------------

    /// <summary>
    /// The Moore–Penrose pseudo-inverse from the rank factorisation A = C·F (C the pivot columns, F the non-zero
    /// rows of the reduced row echelon form): A⁺ = Fᵀ(F·Fᵀ)⁻¹(Cᵀ·C)⁻¹Cᵀ.
    /// </summary>
    public static Expr PseudoInverse(Expr a)
    {
        var m = LinearAlgebra.ToArray(a);
        var (r, pivots) = LinearAlgebra.Rref(m);
        int rows = Rows(m), cols = Cols(m), rank = pivots.Count;
        if (rank == 0)
        {
            var z = new Expr[cols, rows];
            for (int i = 0; i < cols; i++) for (int j = 0; j < rows; j++) z[i, j] = Expr.Zero;
            return Matrix(z);
        }
        var c = FromColumns(pivots.Select(j => Column(m, j)).ToList(), rows);
        var f = new Expr[rank, cols];
        for (int i = 0; i < rank; i++) for (int j = 0; j < cols; j++) f[i, j] = r[i, j];
        Func<Expr, Expr> simp = LinearAlgebra.Simp;
        var ft = Transpose(f); var ct = Transpose(c);
        var ffInv = LinearAlgebra.ToArray(LinearAlgebra.Inverse(Matrix(Mul(f, ft, simp))));
        var ccInv = LinearAlgebra.ToArray(LinearAlgebra.Inverse(Matrix(Mul(ct, c, simp))));
        return Matrix(Mul(Mul(ft, ffInv, simp), Mul(ccInv, ct, simp), simp));
    }

    /// <summary>The least-squares solution of A·x = b (the one of minimum norm when it is not unique).</summary>
    public static Expr LeastSquares(Expr a, Expr b)
    {
        if (!LinearAlgebra.IsVector(b)) throw new MathException(Messages.T("lsq: the right-hand side must be a vector."));
        var m = LinearAlgebra.ToArray(a);
        if (Vector(b).Length != Rows(m)) throw new MathException(Messages.T("Right-hand side length does not match the number of rows."));
        return LinearAlgebra.MapEntries(LinearAlgebra.Dot(PseudoInverse(a), b), x => LinearAlgebra.Simp(Expansion.Expand(x)));
    }

    // ---- eigenstructure: diagonalisation, Jordan form, minimal polynomial ----------------------------------

    private sealed record Block(Expr Eigenvalue, List<Expr[]> Chain);

    /// <summary>A − λI.</summary>
    private static Expr[,] Shift(Expr[,] m, Expr lambda)
    {
        var r = (Expr[,])m.Clone();
        for (int i = 0; i < Rows(m); i++) r[i, i] = LinearAlgebra.Simp(Expansion.Expand(m[i, i] - lambda));
        return r;
    }

    private static Expr[] Apply(Expr[,] m, Expr[] v) =>
        Enumerable.Range(0, Rows(m)).Select(i => LinearAlgebra.Simp(Expansion.Expand(Expr.Plus(Enumerable.Range(0, Cols(m)).Select(k => m[i, k] * v[k]))))).ToArray();

    /// <summary>Jordan chains of A, eigenvalue by eigenvalue, longest chain first; each chain runs from the eigenvector up.</summary>
    private static List<Block> JordanChains(Expr[,] m, string fn)
    {
        int n = Rows(m);
        var values = LinearAlgebra.Eigenvalues(Matrix(m));
        if (values.Any(v => v.ContainsAny(z => z is Real)) || HasReals(m))
            throw new MathException(Messages.T("{0} needs exact eigenvalues; this matrix has floating-point entries or eigenvalues that cannot be found exactly.", fn));
        if (values.Count != n) throw new MathException(Messages.T("Could not compute the eigenvalues."));
        var blocks = new List<Block>();
        foreach (var group in values.GroupBy(v => v))
        {
            var lambda = group.Key;
            int mult = group.Count();
            var nmat = Shift(m, lambda);
            // Kernels of N, N², ... until they reach the algebraic multiplicity.
            var kernels = new List<List<Expr[]>> { new() };
            var power = nmat;
            for (int k = 1; k <= mult; k++)
            {
                var basis = LinearAlgebra.NullSpace(power).Select(Vector).ToList();
                kernels.Add(basis);
                if (basis.Count >= mult) break;
                power = Mul(power, nmat, LinearAlgebra.Simp);
            }
            if (kernels[^1].Count < mult) throw new MathException(Messages.T("Could not compute the eigenvalues."));
            var chosen = new List<List<Expr[]>>();
            for (int level = kernels.Count - 1; level >= 1; level--)
            {
                // Vectors already at this level: lower kernel, and images of the longer chains' tops.
                var span = new List<Expr[]>(kernels[level - 1]);
                foreach (var chain in chosen) span.Add(chain[level - 1]);
                int rank = Rank(span);
                foreach (var v in kernels[level])
                {
                    span.Add(v);
                    int next = Rank(span);
                    if (next == rank) { span.RemoveAt(span.Count - 1); continue; }
                    rank = next;
                    var chain = new Expr[level][];
                    chain[level - 1] = v;
                    for (int i = level - 2; i >= 0; i--) chain[i] = Apply(nmat, chain[i + 1]);
                    chosen.Add([.. chain]);
                }
            }
            foreach (var chain in chosen.OrderByDescending(c => c.Count))
                blocks.Add(new Block(lambda, chain.Select(NiceVector).ToList()));
        }
        return blocks;
    }

    /// <summary>Clears denominators of a rational vector, as the null space does.</summary>
    private static Expr[] NiceVector(Expr[] v)
    {
        if (!v.All(x => x is Num)) return v;
        var lcm = v.Cast<Num>().Aggregate(System.Numerics.BigInteger.One, (acc, x) =>
            acc / System.Numerics.BigInteger.GreatestCommonDivisor(acc, x.Value.Den) * x.Value.Den);
        return lcm.IsOne ? v : v.Select(x => x * new Num(lcm)).ToArray();
    }

    /// <summary>[P, J] with A = P·J·P⁻¹ and J in Jordan normal form.</summary>
    public static Expr Jordan(Expr a)
    {
        var m = SquareArray(a, "jordan");
        var (p, j) = JordanPair(m, "jordan");
        return Expr.List(Matrix(p), Matrix(j));
    }

    private static (Expr[,] P, Expr[,] J) JordanPair(Expr[,] m, string fn)
    {
        int n = Rows(m);
        var blocks = JordanChains(m, fn);
        // Scaling a whole chain keeps it a chain, so rational eigenvectors can be given integer entries; the
        // vectors above the eigenvector are fixed by it and may keep fractions.
        var columns = blocks.SelectMany(b => b.Chain).ToList();
        var p = FromColumns(columns, n);
        var jm = Identity(n);
        int at = 0;
        foreach (var b in blocks)
        {
            for (int i = 0; i < b.Chain.Count; i++)
            {
                for (int c = 0; c < n; c++) jm[at + i, c] = Expr.Zero;
                jm[at + i, at + i] = b.Eigenvalue;
                if (i + 1 < b.Chain.Count) jm[at + i, at + i + 1] = Expr.One;
            }
            at += b.Chain.Count;
        }
        return (p, jm);
    }

    /// <summary>[P, D] with A = P·D·P⁻¹ and D diagonal; fails when A is not diagonalisable.</summary>
    public static Expr Diagonalize(Expr a)
    {
        var m = SquareArray(a, "diagonalize");
        int n = Rows(m);
        var eig = (ListExpr)LinearAlgebra.Eigenvectors(Matrix(m));
        var columns = new List<Expr[]>();
        var diagonal = new List<Expr>();
        foreach (ListExpr entry in eig.Items)
        {
            var lambda = entry.Items[0];
            int mult = (int)((Num)entry.Items[1]).Value.Num;
            var vectors = ((ListExpr)entry.Items[2]).Items;
            if (vectors.Count < mult)
                throw new MathException(Messages.T("The matrix is not diagonalizable: the eigenvalue {0} has multiplicity {1} but only {2} independent eigenvectors. Use jordan(A) instead.", lambda, mult, vectors.Count));
            foreach (var v in vectors) { columns.Add(Vector(v)); diagonal.Add(lambda); }
        }
        if (columns.Count < n) throw new MathException(Messages.T("Could not compute the eigenvalues."));
        var d = Identity(n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) d[i, j] = i == j ? diagonal[i] : Expr.Zero;
        return Expr.List(Matrix(FromColumns(columns, n)), Matrix(d));
    }

    /// <summary>
    /// The minimal polynomial: the first linear dependency among I, A, A², ... (found from the null space of
    /// the matrix whose columns are the flattened powers), so it needs no eigenvalues.
    /// </summary>
    public static Expr MinimalPolynomial(Expr a, Expr x)
    {
        var m = SquareArray(a, "minpoly");
        int n = Rows(m);
        var powers = new List<Expr[]> { Flat(Identity(n)) };
        var current = Identity(n);
        for (int k = 1; k <= n; k++)
        {
            current = Mul(current, m, LinearAlgebra.Simp);
            powers.Add(Flat(current));
            var stacked = FromColumns(powers, n * n);
            var kernel = LinearAlgebra.NullSpace(stacked);
            if (kernel.Count == 0) continue;
            var c = Vector(kernel[0]);
            var lead = c[k];
            return Expansion.Expand(Expr.Plus(Enumerable.Range(0, k + 1).Select(j => LinearAlgebra.Simp(c[j] / lead) * Expr.Power(x, j))));
        }
        throw new MathException(Messages.T("Could not compute the eigenvalues."));
    }

    private static Expr[] Flat(Expr[,] m) => m.Cast<Expr>().ToArray();

    // ---- matrix exponential ------------------------------------------------------------------------------

    /// <summary>exp(A·t) from the Jordan form, or by scaling and squaring when the entries are floating-point.</summary>
    public static Expr Exponential(Expr a, Expr t)
    {
        var m = SquareArray(a, "expm");
        int n = Rows(m);
        if (HasReals(m)) return NumericExponential(m, t);
        (Expr[,] P, Expr[,] J) pj;
        try { pj = JordanPair(m, "expm"); }
        catch (MathException) when (m.Cast<Expr>().All(e => e.IsNumber) && t.IsNumber) { return NumericExponential(m, t); }
        var (p, j) = pj;
        // exp of a Jordan block: e^(λt) t^k / k! on the k-th superdiagonal.
        var e = new Expr[n, n];
        for (int i = 0; i < n; i++) for (int c = 0; c < n; c++) e[i, c] = Expr.Zero;
        int start = 0;
        while (start < n)
        {
            int size = 1;
            while (start + size < n && j[start + size - 1, start + size].IsOne) size++;
            var lambda = j[start, start];
            var exp = Expr.Exp(Expansion.Expand(lambda * t));
            for (int r = 0; r < size; r++)
                for (int c = r; c < size; c++)
                    e[start + r, start + c] = exp * Expr.Power(t, c - r) / Expr.Call("factorial", new Num(c - r));
            start += size;
        }
        var pInv = LinearAlgebra.ToArray(LinearAlgebra.Inverse(Matrix(p)));
        bool real = m.Cast<Expr>().All(x => !x.Contains(Expr.I)) && !t.Contains(Expr.I);
        return Matrix(Mul(Mul(p, e, x => x), pInv, x => ExpEntry(x, real)));
    }

    /// <summary>Simplifies an entry of exp(A·t); for a real matrix, complex exponentials become sines and cosines.</summary>
    private static Expr ExpEntry(Expr e, bool real)
    {
        if (real && e.Contains(Expr.I)) e = Expansion.Expand(EulerRewrite(e));
        return Clean(e);
    }

    /// <summary>e^(a + b·i) → e^a·(cos b + i·sin b).</summary>
    private static Expr EulerRewrite(Expr e) => e.MapBottomUp(z =>
    {
        if (z is not PowExpr { Base: var b } pw || b != Expr.E || !pw.Exponent.Contains(Expr.I)) return z;
        var exponent = Expansion.Expand(pw.Exponent);
        var terms = exponent is AddExpr add ? add.Terms : [exponent];
        var re = new List<Expr>(); var im = new List<Expr>();
        foreach (var term in terms)
        {
            var quotient = Expansion.Expand(term / Expr.I);
            if (quotient.Contains(Expr.I)) re.Add(term); else im.Add(quotient);
        }
        if (im.Count == 0) return z;
        var y = Expr.Plus(im);
        return Expr.Exp(Expr.Plus(re)) * (Expr.Call("cos", y) + Expr.I * Expr.Call("sin", y));
    });

    private static Expr NumericExponential(Expr[,] m, Expr t)
    {
        double tv = t.IsConstant ? Numerics.NumericEval.EvalReal(t) : double.NaN;
        if (double.IsNaN(tv)) throw new MathException(Messages.T("expm: a matrix with floating-point entries needs a numeric value of t."));
        int n = Rows(m);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var v = Numerics.NumericEval.EvalReal(m[i, j]);
                if (double.IsNaN(v)) throw new MathException(Messages.T("expm: the entries must be real numbers."));
                a[i, j] = v * tv;
            }
        // Scaling and squaring with a Taylor polynomial.
        double norm = 0;
        for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += Math.Abs(a[i, j]); norm = Math.Max(norm, s); }
        int squarings = norm > 0.5 ? (int)Math.Ceiling(Math.Log2(norm / 0.5)) : 0;
        double scale = Math.Pow(2, -squarings);
        var x = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) x[i, j] = a[i, j] * scale;
        var result = new double[n, n];
        var term = new double[n, n];
        for (int i = 0; i < n; i++) { result[i, i] = 1; term[i, i] = 1; }
        for (int k = 1; k <= 20; k++)
        {
            term = MulD(term, x);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { term[i, j] /= k; result[i, j] += term[i, j]; }
        }
        for (int s = 0; s < squarings; s++) result = MulD(result, result);
        var r = new Expr[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) r[i, j] = new Real(result[i, j]);
        return Matrix(r);
    }

    private static double[,] MulD(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), p = b.GetLength(1), q = a.GetLength(1);
        var c = new double[n, p];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < p; j++)
            {
                double s = 0;
                for (int k = 0; k < q; k++) s += a[i, k] * b[k, j];
                c[i, j] = s;
            }
        return c;
    }

    // ---- adjugate --------------------------------------------------------------------------------------

    /// <summary>The adjugate (transpose of the cofactor matrix); A·adj(A) = det(A)·I, also for singular A.</summary>
    public static Expr Adjugate(Expr a)
    {
        var m = SquareArray(a, "adjugate");
        int n = Rows(m);
        if (n == 1) return Expr.List(Expr.List(Expr.One));
        var adj = new Expr[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                // adj[i, j] = cofactor C[j, i]
                var minor = new Expr[n - 1, n - 1];
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
                var cof = LinearAlgebra.Determinant(Matrix(minor));
                adj[i, j] = (i + j) % 2 == 1 ? LinearAlgebra.Simp(-cof) : cof;
            }
        return Matrix(adj);
    }

    // ---- singular values and the SVD -----------------------------------------------------------------------

    /// <summary>The singular values in decreasing order (square roots of the eigenvalues of AᵀA), zeros included up to min(m, n).</summary>
    public static List<Expr> SingularValues(Expr a)
    {
        var m = LinearAlgebra.ToArray(a);
        if (HasReals(m)) return NumericSvd(m).S.Select(s => (Expr)new Real(s)).ToList();
        var gram = Rows(m) < Cols(m) ? Mul(m, Transpose(m), LinearAlgebra.Simp) : Mul(Transpose(m), m, LinearAlgebra.Simp);
        var values = LinearAlgebra.Eigenvalues(Matrix(gram));
        return values.Select(v => Clean(Expr.Sqrt(v)))
            .OrderByDescending(v => Numerics.NumericEval.EvalReal(v))
            .ToList();
    }

    /// <summary>
    /// [U, Σ, V] with A = U·Σ·Vᵀ (full SVD: U and V orthogonal). Exact when the eigenvalues of AᵀA are, numerical
    /// otherwise.
    /// </summary>
    public static Expr Svd(Expr a)
    {
        var m = LinearAlgebra.ToArray(a);
        int rows = Rows(m), cols = Cols(m);
        if (!HasReals(m) && ExactSvd(m) is { } exact) return exact;
        var (u, s, v) = NumericSvd(m);
        var sigma = new Expr[rows, cols];
        for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) sigma[i, j] = i == j ? new Real(s[i]) : Expr.Zero;
        return Expr.List(Matrix(ToExpr(u)), Matrix(sigma), Matrix(ToExpr(v)));
    }

    private static Expr[,] ToExpr(double[,] d)
    {
        var r = new Expr[d.GetLength(0), d.GetLength(1)];
        for (int i = 0; i < d.GetLength(0); i++)
            for (int j = 0; j < d.GetLength(1); j++)
                r[i, j] = Math.Abs(d[i, j]) < 1e-14 ? Expr.Zero : new Real(d[i, j]);
        return r;
    }

    private static Expr? ExactSvd(Expr[,] m)
    {
        int rows = Rows(m), cols = Cols(m);
        var gram = Mul(Transpose(m), m, LinearAlgebra.Simp);
        ListExpr eig;
        try { eig = (ListExpr)LinearAlgebra.Eigenvectors(Matrix(gram)); }
        catch (MathException) { return null; }
        var pairs = new List<(Expr Sigma, double Value, List<Expr[]> Vectors)>();
        foreach (ListExpr entry in eig.Items)
        {
            var lambda = entry.Items[0];
            if (lambda.ContainsAny(z => z is Real) || lambda.Contains(Expr.I)) return null;
            var vectors = ((ListExpr)entry.Items[2]).Items.Select(Vector).ToList();
            if (vectors.Count != (int)((Num)entry.Items[1]).Value.Num) return null;
            // Orthonormal eigenvectors within each eigenspace (eigenspaces of a symmetric matrix are orthogonal).
            pairs.Add((Clean(Expr.Sqrt(lambda)), Numerics.NumericEval.EvalReal(lambda), GramSchmidt(vectors, normalize: true)));
        }
        pairs.Sort((x, y) => y.Value.CompareTo(x.Value));
        var vCols = new List<Expr[]>();
        var uCols = new List<Expr[]>();
        var sigmas = new List<Expr>();
        foreach (var (sigma, value, vectors) in pairs)
            foreach (var v in vectors)
            {
                vCols.Add(v);
                sigmas.Add(sigma);
                if (value > 1e-12) uCols.Add(Apply(m, v).Select(x => Clean(x / sigma)).ToArray());
            }
        if (vCols.Count != cols) return null;
        // Complete U to an orthonormal basis of the whole space with the standard basis vectors.
        var completion = new List<Expr[]>(uCols);
        for (int i = 0; i < rows && completion.Count < rows; i++)
        {
            var e = Enumerable.Range(0, rows).Select(k => k == i ? Expr.One : Expr.Zero).ToArray();
            var extended = GramSchmidt([.. completion, e], normalize: true);
            if (extended.Count > completion.Count) completion.Add(extended[^1]);
        }
        var sigmaMatrix = new Expr[rows, cols];
        for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) sigmaMatrix[i, j] = i == j && i < sigmas.Count ? sigmas[i] : Expr.Zero;
        return Expr.List(Matrix(FromColumns(completion, rows)), Matrix(sigmaMatrix), Matrix(FromColumns(vCols, cols)));
    }

    /// <summary>One-sided Jacobi SVD in floating point: U (m×m), the singular values (min(m, n), decreasing), V (n×n).</summary>
    private static (double[,] U, double[] S, double[,] V) NumericSvd(Expr[,] m)
    {
        int rows = Rows(m), cols = Cols(m);
        bool flip = rows < cols;
        int p = flip ? cols : rows, q = flip ? rows : cols;
        var a = new double[p, q];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
            {
                var v = Numerics.NumericEval.EvalReal(m[i, j]);
                if (double.IsNaN(v)) throw new MathException(Messages.T("svd: the entries must be real numbers."));
                if (flip) a[j, i] = v; else a[i, j] = v;
            }
        var v2 = new double[q, q];
        for (int i = 0; i < q; i++) v2[i, i] = 1;
        for (int sweep = 0; sweep < 60; sweep++)
        {
            double off = 0;
            for (int j = 0; j < q - 1; j++)
                for (int k = j + 1; k < q; k++)
                {
                    double alpha = 0, beta = 0, gamma = 0;
                    for (int i = 0; i < p; i++) { alpha += a[i, j] * a[i, j]; beta += a[i, k] * a[i, k]; gamma += a[i, j] * a[i, k]; }
                    if (Math.Abs(gamma) <= 1e-15 * Math.Sqrt(alpha * beta)) continue;
                    off = Math.Max(off, Math.Abs(gamma) / Math.Sqrt(alpha * beta));
                    double zeta = (beta - alpha) / (2 * gamma);
                    double t = Math.Sign(zeta == 0 ? 1 : zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    double c = 1 / Math.Sqrt(1 + t * t), s = c * t;
                    for (int i = 0; i < p; i++) { double x = a[i, j], y = a[i, k]; a[i, j] = c * x - s * y; a[i, k] = s * x + c * y; }
                    for (int i = 0; i < q; i++) { double x = v2[i, j], y = v2[i, k]; v2[i, j] = c * x - s * y; v2[i, k] = s * x + c * y; }
                }
            if (off < 1e-15) break;
        }
        var sigma = new double[q];
        for (int j = 0; j < q; j++) { double s = 0; for (int i = 0; i < p; i++) s += a[i, j] * a[i, j]; sigma[j] = Math.Sqrt(s); }
        var order = Enumerable.Range(0, q).OrderByDescending(j => sigma[j]).ToArray();
        var left = new List<double[]>();
        foreach (var j in order)
            if (sigma[j] > 1e-13 * Math.Max(1, sigma[order[0]]))
                left.Add(Enumerable.Range(0, p).Select(i => a[i, j] / sigma[j]).ToArray());
        CompleteBasis(left, p);
        var u = new double[p, p];
        for (int j = 0; j < p; j++) for (int i = 0; i < p; i++) u[i, j] = left[j][i];
        var right = new double[q, q];
        for (int jj = 0; jj < q; jj++) for (int i = 0; i < q; i++) right[i, jj] = v2[i, order[jj]];
        var sorted = order.Select(j => sigma[j]).ToArray();
        return flip ? (right, sorted, u) : (u, sorted, right);
    }

    private static void CompleteBasis(List<double[]> basis, int n)
    {
        for (int e = 0; e < n && basis.Count < n; e++)
        {
            var w = new double[n];
            w[e] = 1;
            foreach (var b in basis)
            {
                double d = 0;
                for (int i = 0; i < n; i++) d += b[i] * w[i];
                for (int i = 0; i < n; i++) w[i] -= d * b[i];
            }
            double norm = Math.Sqrt(w.Sum(x => x * x));
            if (norm < 1e-8) continue;
            basis.Add(w.Select(x => x / norm).ToArray());
        }
    }

    /// <summary>The 2-norm condition number σ_max / σ_min of a square matrix (∞ when it is singular).</summary>
    public static Expr Condition(Expr a)
    {
        var m = SquareArray(a, "cond");
        if (HasReals(m))
        {
            var s = NumericSvd(m).S;
            return s[^1] < 1e-14 * s[0] ? Expr.Infinity : new Real(s[0] / s[^1]);
        }
        // σ_max/σ_min = sqrt(λ_max/λ_min) for the eigenvalues λ of AᵀA; simplifying the ratio first lets the
        // square root denest ((15 + √221)/2 rather than a quotient of nested radicals).
        var values = LinearAlgebra.Eigenvalues(Matrix(Mul(Transpose(m), m, LinearAlgebra.Simp)))
            .OrderBy(v => Numerics.NumericEval.EvalReal(v)).ToList();
        if (IsZero(values[0])) return Expr.Infinity;
        return Clean(Expr.Sqrt(Clean(values[^1] / values[0])));
    }
}
