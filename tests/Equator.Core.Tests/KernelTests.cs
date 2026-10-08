using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

public class KernelTests
{
    private static string Run(string input)
    {
        var k = new Kernel();
        return TextFormatter.Format(k.EvalText(input));
    }

    [Theory]
    // arithmetic & canonical forms
    [InlineData("1/2 + 1/3", "5/6")]
    [InlineData("2^100", "1267650600228229401496703205376")]
    [InlineData("x + x + y", "2*x + y")]
    [InlineData("x*x*x", "x^3")]
    [InlineData("(x^2)^3", "x^6")]
    [InlineData("sqrt(12)", "2*sqrt(3)")]
    [InlineData("sqrt(-4)", "2*I")]
    [InlineData("I^2", "-1")]
    [InlineData("8^(1/3)", "2")]
    [InlineData("2x - 3x", "-x")]
    [InlineData("a/b * b", "a")]
    [InlineData("-(x + 1)", "-x - 1")]
    [InlineData("10!", "3628800")]
    [InlineData("0.5 + 1/4", "0.75")]
    [InlineData("exp(log(x))", "x")]
    // elementary function values
    [InlineData("sin(pi/6)", "1/2")]
    [InlineData("cos(pi/4)", "sqrt(2)/2")]
    [InlineData("tan(pi/3)", "sqrt(3)")]
    [InlineData("sin(-x)", "-sin(x)")]
    [InlineData("cos(-x)", "cos(x)")]
    [InlineData("atan(1)", "pi/4")]
    [InlineData("log(1)", "0")]
    [InlineData("log(e^3)", "3")]
    [InlineData("abs(-3/4)", "3/4")]
    // algebra
    [InlineData("expand((x + 1)^3)", "x^3 + 3*x^2 + 3*x + 1")]
    [InlineData("expand((a + b)*(a - b))", "a^2 - b^2")]
    [InlineData("factor(x^2 - 1)", "(x - 1)*(x + 1)")]
    [InlineData("factor(x^3 - 6x^2 + 11x - 6)", "(x - 3)*(x - 2)*(x - 1)")]
    [InlineData("factor(2x^2 + 4x + 2)", "2*(x + 1)^2")]
    [InlineData("factor(x^2 - y^2)", "(x - y)*(x + y)")]
    [InlineData("factor(x^4 + 4)", "(x^2 - 2*x + 2)*(x^2 + 2*x + 2)")]
    [InlineData("cancel((x^2 - 1)/(x - 1))", "x + 1")]
    [InlineData("simplify(sin(x)^2 + cos(x)^2)", "1")]
    [InlineData("simplify((x^2 + 2x + 1)/(x + 1))", "x + 1")]
    [InlineData("simplify(2*sin(x)*cos(x))", "sin(2*x)")]
    [InlineData("together(1/x + 1/y)", "(x + y)/(x*y)")]
    [InlineData("apart(1/(x^2 - 1), x)", "1/(2*(x - 1)) - 1/(2*(x + 1))")]
    // solving
    [InlineData("solve(x^2 - 5x + 6 = 0, x)", "[x = 2, x = 3]")]
    [InlineData("solve(x^2 + 1 = 0, x)", "[x = -I, x = I]")]
    [InlineData("solve(2x + 3 = 7, x)", "[x = 2]")]
    [InlineData("solve(x^2 - 2 = 0, x)", "[x = -sqrt(2), x = sqrt(2)]")]
    [InlineData("solve(exp(2x) = 5, x)", "[x = log(5)/2]")]
    [InlineData("solve([x + y = 3, x - y = 1], [x, y])", "[[x = 2, y = 1]]")]
    [InlineData("solve(a*x^2 + b*x + c = 0, x)", "[x = (-b - sqrt(b^2 - 4*a*c))/(2*a), x = (-b + sqrt(b^2 - 4*a*c))/(2*a)]")]
    // calculus
    [InlineData("diff(x^3, x)", "3*x^2")]
    [InlineData("diff(sin(x^2), x)", "2*x*cos(x^2)")]
    [InlineData("diff(x^x, x)", "x^x*(log(x) + 1)")]
    [InlineData("diff(exp(2x), x, 3)", "8*e^(2*x)")]
    [InlineData("integrate(x^2, x)", "x^3/3")]
    [InlineData("integrate(1/x, x)", "log(x)")]
    [InlineData("integrate(cos(x), x)", "sin(x)")]
    [InlineData("integrate(1/(1 + x^2), x)", "atan(x)")]
    [InlineData("integrate(x^2, x, 0, 3)", "9")]
    [InlineData("integrate(sin(x), x, 0, pi)", "2")]
    [InlineData("integrate(exp(-x), x, 0, oo)", "1")]
    [InlineData("integrate(exp(-x^2), x, -oo, oo)", "sqrt(pi)")]
    [InlineData("limit(sin(x)/x, x, 0)", "1")]
    [InlineData("limit((1 + 1/n)^n, n, oo)", "e")]
    [InlineData("limit(1/x, x, 0, \"+\")", "Infinity")]
    [InlineData("limit((x^2 - 1)/(x - 1), x, 1)", "2")]
    [InlineData("limit(x*log(x), x, 0, \"+\")", "0")]
    [InlineData("limit((3x^2 + 1)/(x^2 - 5), x, oo)", "3")]
    [InlineData("sum(k, k, 1, n)", "n*(n + 1)/2")]
    [InlineData("sum(k^2, k, 1, 10)", "385")]
    [InlineData("sum(1/k^2, k, 1, oo)", "pi^2/6")]
    [InlineData("sum(1/2^k, k, 0, oo)", "2")]
    [InlineData("sum(1/(k*(k + 1)), k, 1, oo)", "1")]
    [InlineData("series(exp(x), x, 0, 4)", "x^4/24 + x^3/6 + x^2/2 + x + 1 + O(x^5)")]
    [InlineData("series(sin(x), x, 0, 5)", "x^5/120 - x^3/6 + x + O(x^6)")]
    // linear algebra
    [InlineData("det([[1, 2], [3, 4]])", "-2")]
    [InlineData("inv([[2, 1], [1, 1]])", "[[1, -1], [-1, 2]]")]
    [InlineData("[[1, 2], [3, 4]] * [[0, 1], [1, 0]]", "[[2, 1], [4, 3]]")]
    [InlineData("transpose([[1, 2, 3], [4, 5, 6]])", "[[1, 4], [2, 5], [3, 6]]")]
    [InlineData("eigenvalues([[2, 1], [1, 2]])", "[1, 3]")]
    [InlineData("det([[a, b], [c, d]])", "a*d - b*c")]
    [InlineData("rank([[1, 2], [2, 4]])", "1")]
    [InlineData("linsolve([[2, 1], [1, 3]], [3, 5])", "[4/5, 7/5]")]
    [InlineData("[[1, 2], [3, 4]]^2", "[[7, 10], [15, 22]]")]
    // number theory & lists
    [InlineData("factorint(360)", "[[2, 3], [3, 2], [5, 1]]")]
    [InlineData("isprime(2^31 - 1)", "True")]
    [InlineData("gcd(12, 18)", "6")]
    [InlineData("table(i^2, i, 1, 5)", "[1, 4, 9, 16, 25]")]
    [InlineData("mean([1, 2, 3, 10])", "4")]
    // differential equations
    [InlineData("dsolve(y' = y, y, x)", "y(x) = C1*e^x")]
    [InlineData("dsolve([y' = y, y(0) = 2], y, x)", "y(x) = 2*e^x")]
    [InlineData("dsolve(y'' + y = 0, y, x)", "y(x) = C1*cos(x) + C2*sin(x)")]
    public void Evaluates(string input, string expected) => Assert.Equal(expected, Run(input));

    [Fact]
    public void AssignmentAndFunctions()
    {
        var k = new Kernel();
        k.EvalText("f(x) := x^2 + 1");
        Assert.Equal("10", TextFormatter.Format(k.EvalText("f(3)")));
        Assert.Equal("2*a", TextFormatter.Format(k.EvalText("f'(a)")));
        k.EvalText("a := 5");
        Assert.Equal("26", TextFormatter.Format(k.EvalText("f(a)")));
        k.EvalText("g(n) := if(n <= 1, 1, n*g(n - 1))");
        Assert.Equal("120", TextFormatter.Format(k.EvalText("g(5)")));
        // bound variables are not substituted inside calculus operators
        k.EvalText("x := 7");
        Assert.Equal("2*x", TextFormatter.Format(k.EvalText("diff(x^2, x)")));
    }

    [Fact]
    public void HistoryReferences()
    {
        var k = new Kernel();
        k.Evaluate("x^2");
        k.Evaluate("% + 1");
        Assert.Equal("x^2 + 1", TextFormatter.Format(k.EvalText("%")));
        Assert.Equal("x^2", TextFormatter.Format(k.EvalText("%1")));
    }

    [Fact]
    public void ErrorsAreReported()
    {
        var k = new Kernel();
        var r = k.Evaluate("1 + * 2");
        Assert.Equal(OutputKind.Error, r.Outputs[0].Kind);
        r = k.Evaluate("inv([[1, 2], [2, 4]])");
        Assert.Contains("singular", r.Outputs[0].Text);
    }

    [Fact]
    public void SuppressedStatements()
    {
        var k = new Kernel();
        var r = k.Evaluate("a := 3; b := 4;\nsqrt(a^2 + b^2)");
        Assert.Single(r.Outputs);
        Assert.Equal("5", r.Outputs[0].Text);
    }

    [Fact]
    public void PlotsProduceGraphics()
    {
        var k = new Kernel();
        var r = k.Evaluate("plot([sin(x), cos(x)], x, -pi, pi)");
        Assert.Equal(OutputKind.Plot, r.Outputs[0].Kind);
        Assert.Equal(2, r.Outputs[0].Plot!.Series.Count);
        Assert.Equal(1, r.Outputs[0].Plot!.Series[0].Function!(Math.PI / 2), 12);
        r = k.Evaluate("plot(a*x, x, 0, 1)");
        Assert.Equal(OutputKind.Error, r.Outputs[0].Kind);
    }

    [Fact]
    public void NumericEvaluation()
    {
        Assert.Equal("3.14159265359", Run("N(pi)"));
        Assert.Equal("1.41421356237", Run("N(sqrt(2))"));
        var v = new Kernel().EvalText("nintegrate(sin(x)^2, x, 0, pi)");
        Assert.Equal(Math.PI / 2, ((Real)v).Value, 10);
    }

    [Fact]
    public void LatexOutput()
    {
        Assert.Equal("\\frac{x^{2}}{2}", Strip(LatexFormatter.Format(new Kernel().EvalText("x^2/2"))));
        static string Strip(string s) => s.Replace("{x}^", "x^");
    }

    [Theory]
    [InlineData("x*exp(x)")]
    [InlineData("x^2*sin(x)")]
    [InlineData("log(x)")]
    [InlineData("x*log(x)")]
    [InlineData("atan(x)")]
    [InlineData("sin(x)^2")]
    [InlineData("sin(x)^3")]
    [InlineData("cos(x)^4")]
    [InlineData("sin(x)^2*cos(x)^3")]
    [InlineData("tan(x)")]
    [InlineData("tan(x)^3")]
    [InlineData("sec(x)^3")]
    [InlineData("x*exp(x^2)")]
    [InlineData("sin(x)*cos(x)")]
    [InlineData("exp(x)*sin(x)")]
    [InlineData("exp(2*x)*cos(3*x)")]
    [InlineData("1/(x^2 + 2x + 5)")]
    [InlineData("(x + 1)/(x^2 + 1)")]
    [InlineData("1/(x^3 - x)")]
    [InlineData("(x^3 + 2)/(x^2 - 1)")]
    [InlineData("1/((x - 1)^2*(x + 2))")]
    [InlineData("1/(x^2 + 1)^2")]
    [InlineData("1/sqrt(1 - x^2)")]
    [InlineData("sqrt(1 - x^2)")]
    [InlineData("1/sqrt(x^2 + 4)")]
    [InlineData("sqrt(x^2 + 2x + 5)")]
    [InlineData("x/sqrt(x^2 + 1)")]
    [InlineData("log(x)/x")]
    [InlineData("cos(log(x))/x")]
    [InlineData("sin(2x)*cos(3x)")]
    [InlineData("x^3*exp(-x)")]
    [InlineData("log(x)^2")]
    [InlineData("x*atan(x)")]
    [InlineData("exp(sin(x))*cos(x)")]
    [InlineData("1/(x*log(x))")]
    [InlineData("2^x")]
    [InlineData("sinh(x)*cosh(x)")]
    [InlineData("(2x + 3)^5")]
    [InlineData("1/(2x + 1)^3")]
    [InlineData("sec(x)^2*tan(x)")]
    public void IntegralsDifferentiateBack(string integrand)
    {
        var k = new Kernel();
        var f = k.EvalText(integrand);
        var F = k.EvalText($"integrate({integrand}, x)");
        Assert.False(F is FnExpr { Name: "integrate" }, $"No antiderivative found for {integrand}");
        var dF = Calculus.Derivative.Diff(F, new Sym("x"));
        var g = NumericEval.Compile1(dF - f, "x");
        foreach (var x in new[] { 0.3, 0.7, 1.3, 2.1 })
        {
            var v = g(x);
            if (double.IsNaN(v)) continue; // outside the real domain of a particular antiderivative branch
            Assert.True(Math.Abs(v) < 1e-7, $"d/dx({F}) differs from {integrand} at x={x}: {v}");
        }
    }

    [Theory]
    [InlineData("x^3 - 2x - 5", 1)]
    [InlineData("cos(x) - x", 1)]
    [InlineData("x^5 - x + 1", 1)]
    public void NumericRoots(string f, int realRoots)
    {
        var k = new Kernel();
        var r = (ListExpr)k.EvalText($"solve({f} = 0, x)");
        var fn = NumericEval.Compile1(k.EvalText(f), "x");
        var real = r.Items.Cast<EqExpr>().Where(e => e.Rhs is Real).ToList();
        Assert.Equal(realRoots, real.Count);
        foreach (var e in real) Assert.True(Math.Abs(fn(((Real)e.Rhs).Value)) < 1e-9);
    }
}
