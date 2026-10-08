using System.Numerics;
using Equator.Core.Algebra;
using Equator.Core.Calculus;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

/// <summary>
/// Oracle tests: instead of comparing against hand-written expected strings, each result is checked against
/// an independent computation (numeric quadrature, substitution, finite differences, direct evaluation).
/// They catch wrong answers rather than changes of form.
/// </summary>
public class OracleTests
{
    private static Expr Eval(string s) => new Kernel().EvalText(s);

    private static Complex Num(Expr e, double x, double y = 0.77)
    {
        var vars = new Dictionary<string, Complex> { ["x"] = x, ["y"] = y };
        return NumericEval.Eval(e, vars);
    }

    private static bool Finite(Complex c) => double.IsFinite(c.Real) && double.IsFinite(c.Imaginary);

    // ---- definite integrals against quadrature ------------------------------------------------------

    [Theory]
    [InlineData("x^2*exp(-x)", "0", "oo")]
    [InlineData("1/(1 + x^2)", "-oo", "oo")]
    [InlineData("sin(x)^2", "0", "pi")]
    [InlineData("x*log(x)", "0", "1")]
    [InlineData("1/sqrt(x)", "0", "4")]
    [InlineData("log(x)^2", "0", "1")]
    [InlineData("1/(2 + cos(x))", "0", "2*pi")]
    [InlineData("1/(5 - 4*cos(x))", "0", "2*pi")]
    [InlineData("1/(x^2 + 2x + 2)", "-oo", "oo")]
    [InlineData("x^3/(x^4 + 1)", "0", "1")]
    [InlineData("exp(-x)*sin(x)", "0", "oo")]
    [InlineData("1/(x^3 + 1)", "0", "1")]
    [InlineData("sqrt(1 - x^2)", "-1", "1")]
    [InlineData("abs(x)", "-2", "3")]
    [InlineData("x*exp(x)", "-1", "2")]
    [InlineData("atan(x)", "0", "1")]
    [InlineData("1/sqrt(4 - x^2)", "0", "2")]
    [InlineData("tan(x)", "0", "pi/4")]
    [InlineData("sec(x)", "0", "pi/3")]
    [InlineData("sqrt(x^2)", "-1", "2")]
    [InlineData("1/(x^2 - 4)", "-1", "1")]
    public void DefiniteIntegralsMatchQuadrature(string f, string a, string b)
    {
        var value = Eval($"integrate({f}, x, {a}, {b})");
        Assert.False(value is FnExpr { Name: "integrate" }, $"integrate({f}, x, {a}, {b}) was not evaluated");
        var fn = NumericEval.Compile1(Eval(f), "x");
        double lo = NumericEval.EvalReal(Eval(a == "-oo" ? "-1" : a)), hi = NumericEval.EvalReal(Eval(b == "oo" ? "1" : b));
        if (a == "-oo") lo = double.NegativeInfinity;
        if (b == "oo") hi = double.PositiveInfinity;
        var (expected, _) = double.IsFinite(lo) && double.IsFinite(hi) ? NumericMethods.TanhSinh(fn, lo, hi) : NumericMethods.Integrate(fn, lo, hi);
        var got = NumericEval.Eval(value);
        Assert.True(Complex.Abs(got - expected) < 1e-7 * (1 + Math.Abs(expected)), $"∫ {f} over [{a}, {b}] gave {value} = {got}, quadrature says {expected}");
    }

    [Theory]
    [InlineData("1/x", "-1", "1")]
    [InlineData("1/x^2", "-1", "1")]
    [InlineData("1/(x - 2)", "0", "3")]
    [InlineData("tan(x)", "0", "pi")]
    [InlineData("1/x", "0", "1")]
    public void DivergentIntegralsAreReported(string f, string a, string b)
    {
        var ex = Assert.Throws<MathException>(() => Eval($"integrate({f}, x, {a}, {b})"));
        Assert.Contains("converge", ex.Message.Replace("diverges", "does not converge"));
    }

    // ---- antiderivatives differentiate back ---------------------------------------------------------

    [Theory]
    [InlineData("sqrt(x^2)")]
    [InlineData("(2x + 1)^(3/2)")]
    [InlineData("x^3*sqrt(1 + x^2)")]
    [InlineData("exp(sqrt(x))")]
    [InlineData("1/(x*(log(x)^2 + 1))")]
    [InlineData("x^2*atan(x)")]
    [InlineData("asin(x)")]
    [InlineData("(x + 1)/(x^2 + 2x + 5)^2")]
    [InlineData("1/(1 + exp(x))")]
    [InlineData("sin(x)^3*cos(x)^2")]
    [InlineData("x*sin(x)*cos(x)")]
    [InlineData("1/(x^5 - x)")]
    public void AntiderivativesAreCorrectEverywhere(string integrand)
    {
        var f = Eval(integrand);
        var F = Eval($"integrate({integrand}, x)");
        Assert.False(F is FnExpr { Name: "integrate" }, $"No antiderivative found for {integrand}");
        var dF = Derivative.Diff(F, new Sym("x"));
        int checkedPoints = 0;
        foreach (var x in new[] { -2.3, -0.9, -0.2, 0.3, 0.7, 1.3, 2.1, 3.7 })
        {
            Complex fv, dv;
            try { fv = Num(f, x); dv = Num(dF, x); }
            catch (MathException) { continue; }
            // Only where the integrand is real: elsewhere both sides may sit on different branches.
            if (!Finite(fv) || !Finite(dv) || Math.Abs(fv.Imaginary) > 1e-12) continue;
            checkedPoints++;
            Assert.True(Complex.Abs(dv - fv) < 1e-7 * (1 + Complex.Abs(fv)), $"d/dx({F}) = {dv} but {integrand} = {fv} at x = {x}");
        }
        Assert.True(checkedPoints >= 2, $"too few points to check {integrand}");
    }

    // ---- solve: substitute back --------------------------------------------------------------------

    [Theory]
    [InlineData("x^2 - 5x + 6 = 0", 2)]
    [InlineData("sqrt(x + 3) = x - 3", 1)]
    [InlineData("sqrt(x) + sqrt(x - 5) = 5", 1)]
    [InlineData("sin(x) = cos(x)", 2)]
    [InlineData("sin(x) + cos(x) = 1", 2)]
    [InlineData("2*sin(x)^2 - sin(x) - 1 = 0", 3)]
    [InlineData("exp(2x) - 3*exp(x) + 2 = 0", 2)]
    [InlineData("log(x) + log(x - 1) = log(6)", 1)]
    [InlineData("x^4 - 10x^2 + 1 = 0", 4)]
    [InlineData("x^3 - 2 = 0", 3)]
    [InlineData("x^6 - 1 = 0", 6)]
    [InlineData("abs(x - 1) = 2", 2)]
    [InlineData("x/(x - 1) = 1/(x - 1)", 0)]
    [InlineData("2^x = 3^(x - 1)", 1)]
    [InlineData("x^(1/3) = 2", 1)]
    [InlineData("x^x = 4", 1)]
    public void SolutionsSatisfyTheEquation(string equation, int count)
    {
        var sols = (ListExpr)Eval($"solve({equation}, x)");
        Assert.Equal(count, sols.Items.Count);
        var eq = (EqExpr)Eval(equation);
        foreach (var s in sols.Items.Cast<EqExpr>())
        {
            var residual = NumericEval.Eval((eq.Lhs - eq.Rhs).Substitute(new Sym("x"), s.Rhs));
            var scale = 1 + Complex.Abs(NumericEval.Eval(s.Rhs));
            Assert.True(Complex.Abs(residual) < 1e-9 * scale, $"{s} does not satisfy {equation}: residual {residual}");
        }
    }

    [Theory]
    [InlineData("x^2 - 4 < 0")]
    [InlineData("x^2 - 4 >= 0")]
    [InlineData("(x - 1)/(x + 2) <= 0")]
    [InlineData("x^3 - x >= 0")]
    [InlineData("sqrt(x) < 2")]
    [InlineData("log(x) > 0")]
    [InlineData("abs(x - 1) < 2")]
    [InlineData("(x^2 - 1)^2 > 0")]
    [InlineData("1/(x^2 - 1) > 0")]
    [InlineData("x^2 + 1 < 0")]
    [InlineData("-1 < 2x + 1 <= 5")]
    [InlineData("exp(x) > 2")]
    public void InequalitySolutionsMatchDirectEvaluation(string inequality)
    {
        var k = new Kernel();
        var sol = (ListExpr)k.EvalText($"solve({inequality}, x)");
        var x = new Sym("x");
        var rel = k.EvalText(inequality);
        for (double t = -6.05; t < 6; t += 0.1)
        {
            bool expected = Satisfied(rel, x, t);
            bool got = sol.Items.Any(c => Satisfied(c, x, t));
            Assert.True(expected == got, $"{inequality} at x = {t}: direct {expected}, solution set {got} ({TextFormatter.Format(sol)})");
        }
    }

    private static bool Satisfied(Expr cond, Sym x, double t)
    {
        if (cond is EqExpr eq) return Math.Abs(NumericEval.EvalReal(eq.Rhs) - t) < 1e-12;
        if (cond is Sym { Name: "True" }) return true;
        if (cond is Sym { Name: "False" }) return false;
        var f = (FnExpr)cond;
        var op = ((StrExpr)f.Args[0]).Value;
        double lhs;
        if (f.Args[1] is FnExpr { Name: "@rel" } inner)
        {
            if (!Satisfied(inner, x, t)) return false;
            lhs = Value(inner.Args[2], x, t);
        }
        else lhs = Value(f.Args[1], x, t);
        double rhs = Value(f.Args[2], x, t);
        if (double.IsNaN(lhs) || double.IsNaN(rhs)) return false;
        return op switch { "<" => lhs < rhs, ">" => lhs > rhs, "<=" => lhs <= rhs, ">=" => lhs >= rhs, _ => lhs != rhs };
    }

    private static double Value(Expr e, Sym x, double t)
    {
        if (e == Expr.Infinity) return double.PositiveInfinity;
        if (e.IsInfinite) return double.NegativeInfinity;
        try
        {
            var v = NumericEval.Eval(e.Substitute(x, new Real(t)));
            return Math.Abs(v.Imaginary) > 1e-12 || !double.IsFinite(v.Real) ? double.NaN : v.Real;
        }
        catch (MathException) { return double.NaN; }
    }

    // ---- limits -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sin(x)/x", "oo", "0")]
    [InlineData("(tan(x) - sin(x))/x^3", "0", "1/2")]
    [InlineData("exp(x)/x^100", "oo", "Infinity")]
    [InlineData("x^100*exp(-x)", "oo", "0")]
    [InlineData("n!/n^n", "oo", "0")]
    [InlineData("(n!)^(1/n)/n", "oo", "e^(-1)")]
    [InlineData("n^2/2^n", "oo", "0")]
    [InlineData("2^n/n!", "oo", "0")]
    [InlineData("x*sin(1/x)", "0", "0")]
    [InlineData("log(x)^3/x", "oo", "0")]
    [InlineData("n^(1/n)", "oo", "1")]
    [InlineData("(1 + 2/x)^x", "oo", "e^2")]
    [InlineData("(sqrt(x^2 + x) - x)", "oo", "1/2")]
    [InlineData("(1 - cos(x))/x^2", "0", "1/2")]
    [InlineData("abs(x)/x", "0", "Undefined")]
    [InlineData("sin(1/x)", "0", "Undefined")]
    [InlineData("cos(x)/x", "oo", "0")]
    [InlineData("x^x", "0", "1")]
    public void Limits(string f, string point, string expected)
    {
        var got = Eval($"limit({f}, {(f.Contains('x') ? "x" : "n")}, {point})");
        var want = Eval(expected);
        if (want.IsConstant && !want.IsUndefinedOrInfinite)
        {
            Assert.True(got.IsConstant && !got.IsUndefinedOrInfinite, $"limit of {f} gave {got}");
            Assert.True(Complex.Abs(NumericEval.Eval(got) - NumericEval.Eval(want)) < 1e-12, $"limit of {f}: {got}, expected {expected}");
            Assert.DoesNotContain(got.Subexpressions(), z => z is Real);
        }
        else Assert.Equal(TextFormatter.Format(want), TextFormatter.Format(got));
    }

    // ---- series: the truncation error must scale like h^(n+1) ----------------------------------------

    [Theory]
    [InlineData("exp(x)*sin(x)", 0, 6)]
    [InlineData("tan(x)", 0, 7)]
    [InlineData("log(1 + x)/(1 - x)", 0, 5)]
    [InlineData("sqrt(1 + x)", 0, 4)]
    [InlineData("1/cos(x)", 0, 6)]
    [InlineData("atan(x)", 0, 7)]
    [InlineData("exp(x)", 1, 5)]
    [InlineData("log(x)", 2, 5)]
    public void SeriesTruncationError(string f, double a, int n)
    {
        var fe = Eval(f);
        var s = Eval($"series({f}, x, {a}, {n})");
        var poly = s.MapBottomUp(z => z is FnExpr { Name: "O" } ? Expr.Zero : z);
        double Err(double h) => Complex.Abs(Num(fe, a + h) - Num(poly, a + h));
        double e1 = Err(0.02), e2 = Err(0.01);
        // Halving h divides the error by about 2^(n+1).
        Assert.True(e2 < e1 / Math.Pow(2, n + 1) * 1.6 + 1e-14, $"series of {f}: errors {e1}, {e2}");
    }

    // ---- random expressions: transformations must preserve values ----------------------------------

    private sealed class Gen(int seed)
    {
        private readonly Random _rng = new(seed);
        private static readonly Sym X = new("x"), Y = new("y");

        public Expr Next(int depth)
        {
            if (depth <= 0 || _rng.NextDouble() < 0.25) return Leaf();
            switch (_rng.Next(10))
            {
                case 0: case 1: return Next(depth - 1) + Next(depth - 1);
                case 2: case 3: return Next(depth - 1) * Next(depth - 1);
                case 4: return Next(depth - 1) - Next(depth - 1);
                case 5: return Next(depth - 1) / (Next(depth - 1) + _rng.Next(2, 4));
                case 6: return Expr.Power(Next(depth - 1), _rng.Next(-2, 4));
                case 7: return Expr.Call(new[] { "sin", "cos", "exp" }[_rng.Next(3)], Next(depth - 1));
                case 8: return Expr.Sqrt(Expr.Power(Next(depth - 1), 2) + 1);
                default: return Expr.Call("log", Expr.Power(Next(depth - 1), 2) + 2);
            }
        }

        private Expr Leaf() => _rng.Next(6) switch
        {
            0 or 1 => X,
            2 => Y,
            3 => new Num(new BigRational(_rng.Next(-5, 6), _rng.Next(1, 4))),
            4 => _rng.Next(4),
            _ => X * _rng.Next(1, 4),
        };
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 200).Select(i => new object[] { i });

    private static readonly (double X, double Y)[] Points = [(0.37, 1.21), (-1.13, 0.52), (2.29, -0.81), (-0.61, -1.7)];

    private static void AssertSameValues(Expr original, Expr transformed, string what)
    {
        foreach (var (px, py) in Points)
        {
            Complex a, b;
            try { a = Num(original, px, py); }
            catch (MathException) { continue; }
            if (!Finite(a) || Complex.Abs(a) > 1e8) continue;
            try { b = Num(transformed, px, py); }
            catch (MathException e) { Assert.Fail($"{what}: {original} -> {transformed} cannot be evaluated at ({px}, {py}): {e.Message}"); return; }
            Assert.True(Complex.Abs(a - b) < 1e-8 * (1 + Complex.Abs(a)), $"{what}: {original} = {a} but {transformed} = {b} at ({px}, {py})");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomTransformationsPreserveValues(int seed)
    {
        var e = new Gen(seed).Next(4);
        AssertSameValues(e, Expansion.Expand(e), "expand");
        AssertSameValues(e, Rational.Together(e), "together");
        AssertSameValues(e, Rational.Cancel(e), "cancel");
        AssertSameValues(e, Factoring.Factor(e), "factor");
        AssertSameValues(e, Simplifier.Simplify(e), "simplify");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomDerivativesMatchFiniteDifferences(int seed)
    {
        var e = new Gen(seed + 1000).Next(4);
        var d = Derivative.Diff(e, new Sym("x"));
        foreach (var (px, py) in Points)
        {
            const double h = 1e-5;
            Complex f1, f2, dv;
            try { f1 = Num(e, px + h, py); f2 = Num(e, px - h, py); dv = Num(d, px, py); }
            catch (MathException) { continue; }
            if (!Finite(f1) || !Finite(f2) || !Finite(dv) || Complex.Abs(f1) > 1e6) continue;
            var fd = (f1 - f2) / (2 * h);
            Assert.True(Complex.Abs(fd - dv) < 1e-4 * (1 + Complex.Abs(dv)), $"d/dx {e} = {d}: {dv} vs finite difference {fd} at ({px}, {py})");
        }
    }
}
