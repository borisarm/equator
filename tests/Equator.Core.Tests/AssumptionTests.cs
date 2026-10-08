using System.Numerics;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

public class AssumptionTests
{
    private static string Run(string s) => TextFormatter.Format(new Kernel().EvalText(s));

    [Theory]
    // without assumptions, symbols are complex: nothing that needs realness happens
    [InlineData("sqrt(x^2)", "sqrt(x^2)")]
    [InlineData("re(x)", "re(x)")]
    [InlineData("abs(x^2)", "abs(x^2)")]
    [InlineData("log(exp(x))", "log(e^x)")]
    [InlineData("is(x^2 + 1 > 0)", "Unknown")]
    // with assumptions
    [InlineData("assume(x > 0); sqrt(x^2)", "x")]
    [InlineData("assume(x, real); sqrt(x^2)", "abs(x)")]
    [InlineData("assume(x < 0); abs(x)", "-x")]
    [InlineData("assume(x < 0); sqrt(x^2)", "-x")]
    [InlineData("assume(x, real); re(x) + im(x)", "x")]
    [InlineData("assume(x, real); log(exp(x))", "x")]
    [InlineData("assume(x, real); is(x^2 + 1 > 0)", "True")]
    [InlineData("assume(x > 0); x + 1 > 0", "True")]
    [InlineData("assume(x > 2); sign(1 - x)", "-1")]
    [InlineData("assume(n, integer); sin(n*pi) + cos(n*pi)", "(-1)^n")]
    [InlineData("assume(n, integer); (-1)^(2*n)", "1")]
    [InlineData("assume(n, integer); cos((2*n + 1)*pi/2)", "0")]
    [InlineData("assume(n, integer); floor(n) + ceil(n)", "2*n")]
    [InlineData("assume(a > 0); sqrt(4*a^2*b)", "2*a*sqrt(b)")]
    [InlineData("assume(x > 0); solve(x^2 = 4, x)", "[x = 2]")]
    [InlineData("assume(x, integer); solve(2x^2 - 5x + 2 = 0, x)", "[x = 2]")]
    [InlineData("assume(x, real); solve(x^3 = 8, x)", "[x = 2]")]
    [InlineData("assume(a > 0); integrate(exp(-a*x), x, 0, oo)", "1/a")]
    [InlineData("assume(a > 0); integrate(1/(x^2 + a^2), x, -oo, oo)", "pi/a")]
    [InlineData("assume(a > 0); integrate(exp(-a*x^2), x, -oo, oo)", "sqrt(pi)/sqrt(a)")]
    [InlineData("assume(a > 0); limit(exp(-a*x), x, oo)", "0")]
    [InlineData("simplify(log(x*y) - log(x) - log(y), x > 0, y > 0)", "0")]
    [InlineData("refine(sqrt(x^2)*abs(y), x > 0, y < 0)", "-x*y")]
    [InlineData("assume(x > 0); forget(x); sqrt(x^2)", "sqrt(x^2)")]
    // bound variables: integration and limits are over the reals, sum indices are integers
    [InlineData("integrate(1/(1 + exp(x)), x)", "x - log(e^x + 1)")]
    [InlineData("integrate(sqrt(x^2), x)", "x*abs(x)/2")]
    [InlineData("integrate(sqrt(x^2), x, -1, 2)", "5/2")]
    [InlineData("sum(cos(k*pi)/k^2, k, 1, oo)", "-pi^2/12")]
    [InlineData("sum((-1)^(k + 1)/k, k, 1, oo)", "log(2)")]
    [InlineData("abs(3 + 4*I)", "5")]
    [InlineData("arg(1 + I)", "pi/4")]
    public void Refinements(string input, string expected) => Assert.Equal(expected, Run(input));

    [Fact]
    public void ContradictionsAreRejected()
    {
        var k = new Kernel();
        k.EvalText("assume(x > 0)");
        Assert.Throws<MathException>(() => k.EvalText("assume(x < -1)"));
    }

    // ---- oracle: refinement under assumptions never changes values that satisfy them ----------------

    private sealed class Gen(int seed)
    {
        private readonly Random _rng = new(seed);
        private static readonly Sym X = new("x"), Y = new("y");

        public Expr Next(int depth)
        {
            if (depth <= 0 || _rng.NextDouble() < 0.2) return Leaf();
            return _rng.Next(12) switch
            {
                0 or 1 => Next(depth - 1) + Next(depth - 1),
                2 or 3 => Next(depth - 1) * Next(depth - 1),
                4 => Expr.Sqrt(Expr.Power(Next(depth - 1), 2)),
                5 => Expr.Power(Expr.Power(Next(depth - 1), 2 * _rng.Next(1, 3)), new Num(new BigRational(_rng.Next(1, 4), 2 * _rng.Next(1, 3)))),
                6 => Expr.Call("abs", Next(depth - 1)),
                7 => Expr.Call("log", Expr.Exp(Next(depth - 1))),
                8 => Expr.Sqrt(Next(depth - 1) * Next(depth - 1)),
                9 => Expr.Call("sign", Next(depth - 1)),
                10 => Expr.Call(_rng.Next(2) == 0 ? "re" : "im", Next(depth - 1)),
                _ => Expr.Power(Next(depth - 1), _rng.Next(-2, 4)),
            };
        }

        private Expr Leaf() => _rng.Next(5) switch
        {
            0 or 1 => X,
            2 => Y,
            3 => new Num(new BigRational(_rng.Next(-4, 5), _rng.Next(1, 3))),
            _ => X - _rng.Next(1, 3),
        };
    }

    private static readonly (string Condition, double[] Xs)[] Scenarios =
    [
        ("x > 0", [0.3, 1.7, 4.2]),
        ("x < 0", [-0.4, -1.9, -3.3]),
        ("x > 2", [2.1, 3.5, 7.0]),
        ("-1 < x < 1", [-0.8, 0.1, 0.9]),
        ("x, real", [-2.2, 0.6, 3.1]),
    ];

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 300).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RefinementPreservesValuesWhereAssumptionsHold(int seed)
    {
        var e = new Gen(seed).Next(4);
        var (cond, xs) = Scenarios[seed % Scenarios.Length];
        var k = new Kernel();
        k.EvalText($"assume({cond}); assume(y, real)");
        Expr refined;
        using (Assumptions.Scope(k.Assumptions)) refined = Assumptions.Refine(e);
        foreach (var x in xs)
            foreach (var y in new[] { -1.3, 0.7 })
            {
                var vars = new Dictionary<string, Complex> { ["x"] = x, ["y"] = y };
                Complex a, b;
                try { a = NumericEval.Eval(e, vars); }
                catch (MathException) { continue; }
                if (!double.IsFinite(a.Real) || !double.IsFinite(a.Imaginary) || Complex.Abs(a) > 1e8) continue;
                try { b = NumericEval.Eval(refined, vars); }
                catch (MathException ex) { Assert.Fail($"{e} refined to {refined} under {cond} cannot be evaluated at x = {x}: {ex.Message}"); return; }
                Assert.True(Complex.Abs(a - b) < 1e-9 * (1 + Complex.Abs(a)), $"{e} = {a} but refined (under {cond}) {refined} = {b} at x = {x}, y = {y}");
            }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void InferredRangesContainActualValues(int seed)
    {
        var e = new Gen(seed + 7000).Next(3);
        var (cond, xs) = Scenarios[seed % Scenarios.Length];
        var k = new Kernel();
        k.EvalText($"assume({cond}); assume(y > -2, y < 1)");
        Interval range;
        using (Assumptions.Scope(k.Assumptions)) range = Assumptions.Range(e);
        if (!range.Real) return;
        foreach (var x in xs)
            foreach (var y in new[] { -1.3, 0.7 })
            {
                Complex v;
                try { v = NumericEval.Eval(e, new Dictionary<string, Complex> { ["x"] = x, ["y"] = y }); }
                catch (MathException) { continue; }
                if (!double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary)) continue;
                Assert.True(Math.Abs(v.Imaginary) < 1e-9, $"{e} was inferred real under {cond} but is {v} at x = {x}");
                double tol = 1e-9 * (1 + Math.Abs(v.Real));
                Assert.True(v.Real >= range.Lo - tol && v.Real <= range.Hi + tol, $"{e} = {v.Real} at x = {x}, y = {y} lies outside the inferred range [{range.Lo}, {range.Hi}] under {cond}");
            }
    }

    [Theory]
    [InlineData("exp(-a*x)", "0", "oo")]
    [InlineData("x*exp(-a*x)", "0", "oo")]
    [InlineData("1/(x^2 + a^2)", "-oo", "oo")]
    [InlineData("exp(-a*x^2)", "-oo", "oo")]
    [InlineData("1/sqrt(a^2 - x^2)", "0", "a")]
    public void ParametricDefiniteIntegralsMatchQuadrature(string f, string lo, string hi)
    {
        var k = new Kernel();
        k.EvalText("assume(a > 0)");
        var value = k.EvalText($"integrate({f}, x, {lo}, {hi})");
        Assert.False(value is FnExpr { Name: "integrate" }, $"not evaluated: {value}");
        foreach (var av in new[] { 0.7, 1.9 })
        {
            var at = value.Substitute(new Sym("a"), new Real(av));
            var fn = NumericEval.Compile1(k.EvalText(f).Substitute(new Sym("a"), new Real(av)), "x");
            double L = lo == "-oo" ? double.NegativeInfinity : lo == "a" ? av : double.Parse(lo);
            double H = hi == "oo" ? double.PositiveInfinity : hi == "a" ? av : double.Parse(hi);
            var (q, _) = double.IsFinite(L) && double.IsFinite(H) ? NumericMethods.TanhSinh(fn, L, H) : NumericMethods.Integrate(fn, L, H);
            Assert.True(Math.Abs(NumericEval.EvalReal(at) - q) < 1e-7 * (1 + Math.Abs(q)), $"∫ {f} = {value} at a = {av}: {NumericEval.EvalReal(at)} vs {q}");
        }
    }
}
