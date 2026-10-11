using System.Numerics;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

/// <summary>
/// Matrix factorisations checked by their defining properties rather than by their printed form: the factors
/// multiply back to the matrix, Q and U are orthogonal, triangular factors are triangular, the pseudo-inverse
/// satisfies the Penrose conditions, the minimal polynomial annihilates the matrix, and so on.
/// </summary>
public class DecompositionTests
{
    private static Expr Eval(string s) => new Kernel().EvalText(s);
    private static string Run(string s) => TextFormatter.Format(Eval(s));

    private static readonly Dictionary<string, Complex> Values = new() { ["a"] = 1.3, ["b"] = -0.7, ["c"] = 2.1, ["d"] = 0.4, ["t"] = 0.6 };

    private static Complex[,] M(Expr e)
    {
        var rows = ((ListExpr)e).Items.Cast<ListExpr>().ToList();
        var m = new Complex[rows.Count, rows[0].Items.Count];
        for (int i = 0; i < rows.Count; i++)
            for (int j = 0; j < rows[0].Items.Count; j++)
                m[i, j] = NumericEval.Eval(rows[i].Items[j], Values);
        return m;
    }

    private static Complex[,] Mul(Complex[,] a, Complex[,] b)
    {
        Assert.Equal(a.GetLength(1), b.GetLength(0));
        var c = new Complex[a.GetLength(0), b.GetLength(1)];
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < b.GetLength(1); j++)
                for (int k = 0; k < a.GetLength(1); k++)
                    c[i, j] += a[i, k] * b[k, j];
        return c;
    }

    private static Complex[,] T(Complex[,] a)
    {
        var t = new Complex[a.GetLength(1), a.GetLength(0)];
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                t[j, i] = a[i, j];
        return t;
    }

    private static Complex[,] Id(int n)
    {
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++) m[i, i] = 1;
        return m;
    }

    private static void Same(Complex[,] x, Complex[,] y, string what)
    {
        Assert.Equal(x.GetLength(0), y.GetLength(0));
        Assert.Equal(x.GetLength(1), y.GetLength(1));
        for (int i = 0; i < x.GetLength(0); i++)
            for (int j = 0; j < x.GetLength(1); j++)
                Assert.True(Complex.Abs(x[i, j] - y[i, j]) < 1e-8 * (1 + Complex.Abs(y[i, j])), $"{what}: entry ({i + 1}, {j + 1}) is {x[i, j]}, expected {y[i, j]}");
    }

    private static void Triangular(Complex[,] m, bool upper, string what)
    {
        for (int i = 0; i < m.GetLength(0); i++)
            for (int j = 0; j < m.GetLength(1); j++)
                if (upper ? i > j : i < j) Assert.True(Complex.Abs(m[i, j]) < 1e-10, $"{what} is not triangular at ({i + 1}, {j + 1})");
    }

    private static Expr Part(Expr list, int i) => ((ListExpr)list).Items[i];

    public static TheoryData<string> Square() =>
    [
        "[[2, 1, 1], [4, 3, 3], [8, 7, 9]]",
        "[[0, 1], [1, 1]]",
        "[[1, 2], [3, 4]]",
        "[[4, 1], [2, 3]]",
        "[[0, 2, 1], [1, 1, 0], [3, 0, 1]]",
        "[[1, 2, 3], [2, 4, 6], [1, 0, 1]]",
        "[[a, b], [c, d]]",
        "[[2, -1, 0], [-1, 2, -1], [0, -1, 2]]",
        "[[5, 4, 2, 1], [0, 1, -1, -1], [-1, -1, 3, 0], [1, 1, -1, 2]]",
    ];

    public static TheoryData<string> Rectangular() =>
    [
        "[[1, 2, 3], [2, 4, 7]]",
        "[[1, 0], [1, 1], [1, 2]]",
        "[[3, 2, 2], [2, 3, -2]]",
        "[[1, 1], [1, 1], [0, 0]]",
        "[[1, 2, 3]]",
        "[[1, 2], [2, 4], [3, 6]]",
        "[[0, 0], [0, 0]]",
        "[[1, 2], [3, 4]]",
    ];

    // ---- factorisations --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Square))]
    [MemberData(nameof(Rectangular))]
    public void LuReassembles(string a)
    {
        var f = Eval($"lu({a})");
        Complex[,] l = M(Part(f, 0)), u = M(Part(f, 1)), p = M(Part(f, 2));
        Same(Mul(p, M(Eval(a))), Mul(l, u), $"P·A = L·U for {a}");
        Triangular(l, upper: false, "L");
        Triangular(u, upper: true, "U");
        for (int i = 0; i < l.GetLength(0); i++) Assert.Equal(1, l[i, i].Real, 10);
    }

    [Fact]
    public void AListOfMatricesIsNotAMatrix()
    {
        // [L, U, P] of three 3×3 matrices has the shape of a 3×3 matrix of rows; it must display as a list.
        Assert.False(((ListExpr)Eval("lu([[2, 1, 1], [4, 3, 3], [8, 7, 9]])")).IsMatrix);
        Assert.True(((ListExpr)Eval("[[1, 2], [3, 4]]")).IsMatrix);
    }

    [Theory]
    [InlineData("[[3, 1], [4, 2]]")]
    [InlineData("[[1, 1, 0], [1, 0, 1], [0, 1, 1]]")]
    [InlineData("[[1, 0], [1, 1], [1, 2]]")]
    [InlineData("[[2, -1, 0], [-1, 2, -1], [0, -1, 2]]")]
    public void QrReassembles(string a)
    {
        var f = Eval($"qr({a})");
        Complex[,] q = M(Part(f, 0)), r = M(Part(f, 1));
        Same(Mul(q, r), M(Eval(a)), $"Q·R = A for {a}");
        Same(Mul(T(q), q), Id(q.GetLength(1)), "QᵀQ = I");
        Triangular(r, upper: true, "R");
    }

    [Theory]
    [InlineData("[[4, 2], [2, 3]]")]
    [InlineData("[[2, -1, 0], [-1, 2, -1], [0, -1, 2]]")]
    [InlineData("[[25, 15, -5], [15, 18, 0], [-5, 0, 11]]")]
    public void CholeskyReassembles(string a)
    {
        var l = M(Eval($"cholesky({a})"));
        Same(Mul(l, T(l)), M(Eval(a)), $"L·Lᵀ = A for {a}");
        Triangular(l, upper: false, "L");
    }

    [Theory]
    [MemberData(nameof(Rectangular))]
    [InlineData("[[1.0, 2], [3, 4]]")]
    [InlineData("[[2, 0], [0, -3]]")]
    [InlineData("[[1, 2, 0], [2, 1, 0], [0, 0, 5]]")]
    public void SvdReassembles(string a)
    {
        var f = Eval($"svd({a})");
        Complex[,] u = M(Part(f, 0)), s = M(Part(f, 1)), v = M(Part(f, 2));
        Same(Mul(Mul(u, s), T(v)), M(Eval(a)), $"U·Σ·Vᵀ = A for {a}");
        Same(Mul(T(u), u), Id(u.GetLength(0)), "UᵀU = I");
        Same(Mul(T(v), v), Id(v.GetLength(0)), "VᵀV = I");
        for (int i = 0; i < s.GetLength(0); i++)
            for (int j = 0; j < s.GetLength(1); j++)
                if (i == j)
                {
                    Assert.True(s[i, i].Real >= -1e-12, "singular values are not negative");
                    if (i > 0) Assert.True(s[i, i].Real <= s[i - 1, i - 1].Real + 1e-12, "singular values decrease");
                }
                else Assert.True(Complex.Abs(s[i, j]) < 1e-12, "Σ is diagonal");
    }

    [Fact]
    public void ExactSvdOfTextbookExample()
    {
        Assert.Equal("[5, 3]", Run("singularvalues([[3, 2, 2], [2, 3, -2]])"));
        Assert.Equal("[3*sqrt(5), sqrt(5)]", Run("singularvalues([[3, 0], [4, 5]])"));
        Assert.Equal("(15 + sqrt(221))/2", Run("cond([[1, 2], [3, 4]])"));
        Assert.Equal("Infinity", Run("cond([[1, 2], [2, 4]])"));
    }

    // ---- eigenstructure ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("[[4, 1], [2, 3]]")]
    [InlineData("[[2, 0, 0], [0, 3, 4], [0, 4, 9]]")]
    [InlineData("[[1, 2], [3, 4]]")]
    [InlineData("[[0, -1], [1, 0]]")]
    [InlineData("[[a, b], [b, a]]")]
    public void DiagonalizeReassembles(string a)
    {
        var f = Eval($"diagonalize({a})");
        Complex[,] p = M(Part(f, 0)), d = M(Part(f, 1));
        Same(Mul(M(Eval(a)), p), Mul(p, d), $"A·P = P·D for {a}");
        Triangular(d, upper: true, "D");
        Triangular(d, upper: false, "D");
    }

    [Fact]
    public void DefectiveMatrixIsNotDiagonalizable()
    {
        var ex = Assert.Throws<MathException>(() => Eval("diagonalize([[1, 1], [0, 1]])"));
        Assert.Contains("jordan", ex.Message);
    }

    [Theory]
    [InlineData("[[5, 4, 2, 1], [0, 1, -1, -1], [-1, -1, 3, 0], [1, 1, -1, 2]]", "[[1, 0, 0, 0], [0, 2, 0, 0], [0, 0, 4, 1], [0, 0, 0, 4]]")]
    [InlineData("[[1, 1], [0, 1]]", "[[1, 1], [0, 1]]")]
    [InlineData("[[a, 1], [0, a]]", "[[a, 1], [0, a]]")]
    [InlineData("[[1, 1, 1], [0, 1, 0], [0, 0, 1]]", "[[1, 1, 0], [0, 1, 0], [0, 0, 1]]")]
    [InlineData("[[3, 1, 0], [0, 3, 1], [0, 0, 3]]", "[[3, 1, 0], [0, 3, 1], [0, 0, 3]]")]
    [InlineData("[[2, 2, 3], [1, 3, 3], [-1, -2, -2]]", "[[1, 1, 0], [0, 1, 0], [0, 0, 1]]")]
    [InlineData("[[0, 0], [0, 0]]", "[[0, 0], [0, 0]]")]
    public void JordanForm(string a, string j)
    {
        var f = Eval($"jordan({a})");
        Assert.Equal(j, TextFormatter.Format(Part(f, 1)));
        var p = M(Part(f, 0));
        Same(Mul(M(Eval(a)), p), Mul(p, M(Part(f, 1))), $"A·P = P·J for {a}");
        Assert.True(Complex.Abs(NumericEval.Eval(Eval($"det({TextFormatter.Format(Part(f, 0))})"), Values)) > 1e-9, "P is invertible");
    }

    [Theory]
    [InlineData("[[2, 0, 0], [0, 2, 0], [0, 0, 3]]", "x^2 - 5*x + 6")]
    [InlineData("[[1, 1], [0, 1]]", "x^2 - 2*x + 1")]
    [InlineData("[[2, 2, 3], [1, 3, 3], [-1, -2, -2]]", "x^2 - 2*x + 1")]
    [InlineData("[[0, 0], [0, 0]]", "x")]
    [InlineData("[[1, 2], [3, 4]]", "x^2 - 5*x - 2")]
    public void MinimalPolynomial(string a, string expected)
    {
        Assert.Equal(expected, Run($"minpoly({a}, x)"));
        // p(A) = 0 by Horner's rule on the matrix.
        var coeffs = ((ListExpr)Eval($"coeffs({expected}, x)")).Items.Select(c => NumericEval.Eval(c)).ToList();
        var m = M(Eval(a));
        int n = m.GetLength(0);
        var acc = new Complex[n, n];
        for (int k = coeffs.Count - 1; k >= 0; k--)
        {
            acc = Mul(acc, m);
            for (int i = 0; i < n; i++) acc[i, i] += coeffs[k];
        }
        Same(acc, new Complex[n, n], "p(A) = 0");
    }

    [Theory]
    [InlineData("[[0, 1], [-1, 0]]", "[[cos(t), sin(t)], [-sin(t), cos(t)]]")]
    [InlineData("[[1, 1], [0, 1]]", "[[e^t, t*e^t], [0, e^t]]")]
    [InlineData("[[1, -2], [2, 1]]", "[[e^t*cos(2*t), -e^t*sin(2*t)], [e^t*sin(2*t), e^t*cos(2*t)]]")]
    [InlineData("[[a, 0], [0, b]]", "[[e^(a*t), 0], [0, e^(b*t)]]")]
    public void MatrixExponentialExactForms(string a, string expected) => Assert.Equal(expected, Run($"expm({a}, t)"));

    [Theory]
    [InlineData("[[1, 2], [3, 4]]")]
    [InlineData("[[2, 2, 3], [1, 3, 3], [-1, -2, -2]]")]
    [InlineData("[[0, 1, 0], [0, 0, 1], [-6, -11, -6]]")]
    [InlineData("[[1, -2], [2, 1]]")]
    [InlineData("[[0.5, 1], [0, 0.5]]")]
    public void MatrixExponentialMatchesTaylorSeries(string a)
    {
        var got = M(Eval($"expm({a}, 0.6)"));
        var m = M(Eval(a));
        int n = m.GetLength(0);
        var sum = Id(n);
        var term = Id(n);
        for (int k = 1; k < 40; k++)
        {
            term = Mul(term, m);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) term[i, j] *= 0.6 / k;
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) sum[i, j] += term[i, j];
        }
        Same(got, sum, $"expm({a}, 0.6)");
    }

    // ---- subspaces, pseudo-inverse, least squares, adjugate -----------------------------------------------

    [Theory]
    [MemberData(nameof(Rectangular))]
    [MemberData(nameof(Square))]
    public void PseudoInverseSatisfiesPenroseConditions(string a)
    {
        var x = M(Eval($"pinv({a})"));
        var m = M(Eval(a));
        Same(Mul(Mul(m, x), m), m, "A·X·A = A");
        Same(Mul(Mul(x, m), x), x, "X·A·X = X");
        Same(T(Mul(m, x)), Mul(m, x), "A·X symmetric");
        Same(T(Mul(x, m)), Mul(x, m), "X·A symmetric");
    }

    [Theory]
    [InlineData("[[1, 0], [1, 1], [1, 2]]", "[6, 0, 0]", "[5, -3]")]
    [InlineData("[[1, 1], [1, 1]]", "[1, 3]", "[1, 1]")]
    [InlineData("[[2, 1], [1, 3]]", "[3, 5]", "[4/5, 7/5]")]
    public void LeastSquares(string a, string b, string expected) => Assert.Equal(expected, Run($"lsq({a}, {b})"));

    [Theory]
    [MemberData(nameof(Square))]
    public void AdjugateTimesMatrixIsDeterminant(string a)
    {
        var adj = M(Eval($"adjugate({a})"));
        var det = NumericEval.Eval(Eval($"det({a})"), Values);
        var expected = Id(adj.GetLength(0));
        for (int i = 0; i < adj.GetLength(0); i++) expected[i, i] = det;
        Same(Mul(M(Eval(a)), adj), expected, $"A·adj(A) = det(A)·I for {a}");
    }

    [Fact]
    public void GramSchmidtGivesAnOrthonormalBasisOfTheSameSpan()
    {
        Assert.Equal("[[sqrt(2)/2, sqrt(2)/2, 0], [sqrt(6)/6, -sqrt(6)/6, sqrt(6)/3]]", Run("gramschmidt([[1, 1, 0], [1, 0, 1]])"));
        Assert.Equal("[[1, 1, 0], [1/2, -1/2, 1]]", Run("gramschmidt([[1, 1, 0], [1, 0, 1]], False)"));
        // Dependent vectors are dropped.
        var q = M(Eval("gramschmidt([[1, 2, 2], [2, 4, 4], [1, 0, 1], [0, 1, 0]])"));
        Assert.Equal(3, q.GetLength(0));
        Same(Mul(q, T(q)), Id(3), "orthonormal");
    }

    [Fact]
    public void SubspacesAndProjection()
    {
        Assert.Equal("[[1, 2], [3, 7]]", Run("columnspace([[1, 2, 3], [2, 4, 7]])"));
        Assert.Equal("[[1, 2, 0], [0, 0, 1]]", Run("rowspace([[1, 2, 3], [2, 4, 7]])"));
        Assert.Equal("[3/2, 3/2, 0]", Run("proj([1, 2, 3], [1, 1, 0])"));
        Assert.Equal("[[-2, 1]]", Run("kernel([[1, 2], [2, 4]])"));
    }

    [Fact]
    public void SymbolicEigenvaluesKeepMultiplicityAndFactor()
    {
        Assert.Equal("[a, a]", Run("eigenvalues([[a, 1], [0, a]])"));
        Assert.Equal("[a, b]", Run("eigenvalues([[a, 0], [0, b]])"));
        Assert.Equal("[a + b, a - b]", Run("eigenvalues([[a, b], [b, a]])"));
        Assert.Equal("[1, 2, 2]", Run("eigenvalues([[2, 0, 0], [1, 2, 0], [0, 0, 1]])"));
    }

    [Theory]
    [InlineData("cholesky([[1, 2], [2, 1]])", "positive definite")]
    [InlineData("cholesky([[1, 2], [3, 4]])", "symmetric")]
    [InlineData("qr([[1, 2], [2, 4]])", "linearly independent")]
    [InlineData("jordan([[1, 2, 3]])", "square")]
    [InlineData("proj([1, 2], [0, 0])", "zero vector")]
    public void Errors(string input, string fragment) => Assert.Contains(fragment, Assert.Throws<MathException>(() => Eval(input)).Message);
}
