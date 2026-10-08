using System.Numerics;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

/// <summary>Arbitrary-precision arithmetic: reference digits, identities at high precision, agreement with machine evaluation.</summary>
public class PrecisionTests
{
    private static string Run(string s) => TextFormatter.Format(new Kernel().EvalText(s));

    [Theory]
    [InlineData("N(pi, 50)", "3.1415926535897932384626433832795028841971693993751")]
    [InlineData("N(e, 50)", "2.7182818284590452353602874713526624977572470937000")]
    [InlineData("N(sqrt(2), 40)", "1.414213562373095048801688724209698078570")]
    [InlineData("N(log(2), 40)", "0.6931471805599453094172321214581765680755")]
    [InlineData("N(zeta(3), 40)", "1.202056903159594285399738161511449990765")]
    [InlineData("N(gamma(1/3), 30)", "2.67893853470774763365569294097")]
    [InlineData("N(exp(pi*sqrt(163)), 40)", "262537412640768743.9999999999992500725972")]
    [InlineData("N(atan(1/239), 30)", "0.00418407600207472386453821495929")]
    [InlineData("N(erf(1/2), 30)", "0.520499877813046537682746653892")]
    [InlineData("N(1/7, 30)", "0.142857142857142857142857142857")]
    [InlineData("N(10^30 + 1, 35)", "1000000000000000000000000000001.0000")]
    public void ReferenceDigits(string input, string expected) => Assert.Equal(expected, Run(input));

    /// <summary>Expressions that are identically zero must evaluate to (almost) zero at 100 digits.</summary>
    [Theory]
    [InlineData("sin(x)^2 + cos(x)^2 - 1")]
    [InlineData("exp(log(x)) - x")]
    [InlineData("tan(atan(x)) - x")]
    [InlineData("gamma(x + 1) - x*gamma(x)")]
    [InlineData("sinh(x)^2 - cosh(x)^2 + 1")]
    [InlineData("asin(sin(x/4)) - x/4")]
    [InlineData("exp(x)*exp(-x) - 1")]
    [InlineData("log(x^3) - 3*log(x)")]
    [InlineData("atan(x) + atan(1/x) - pi/2")]
    [InlineData("sqrt(x)^2 - x")]
    [InlineData("erf(x) + erf(-x)")]
    [InlineData("gamma(x)*gamma(1 - x) - pi/sin(pi*x)")]
    public void IdentitiesHoldAtHighPrecision(string identity)
    {
        var f = new Kernel().EvalText(identity);
        foreach (var x in new[] { new BigRational(7, 10), new BigRational(13, 10), new BigRational(29, 10) })
        {
            var v = BigEval.Eval(f, BigFloat.DigitsToBits(110), new Dictionary<string, BigComplex> { ["x"] = BigComplex.Real(BigFloat.FromRational(x, BigFloat.DigitsToBits(110))) });
            var magnitude = v.Abs(400);
            Assert.True(magnitude.IsZero || magnitude.Log2Magnitude < -320, $"{identity} at x = {x}: {magnitude}");
        }
    }

    public static IEnumerable<object[]> FunctionPoints()
    {
        string[] fns = ["sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "asinh", "acosh", "atanh", "log", "exp", "sqrt", "gamma", "erf", "zeta", "abs"];
        string[] args = ["0.3", "1.7", "-0.4", "-2.5", "3", "0.5 + 0.8*I", "-1.2 - 0.3*I"];
        foreach (var f in fns)
            foreach (var a in args)
                yield return [f, a];
    }

    [Theory]
    [MemberData(nameof(FunctionPoints))]
    public void MachineAndArbitraryPrecisionAgree(string fn, string arg)
    {
        var k = new Kernel();
        var argExpr = k.EvalText(arg);
        System.Numerics.Complex machine;
        try { machine = NumericEval.EvalFunction(fn, [NumericEval.Eval(argExpr)]); }
        catch (MathException) { return; } // not supported in machine precision for this argument
        if (!double.IsFinite(machine.Real) || !double.IsFinite(machine.Imaginary)) return;
        BigComplex big;
        try { big = BigEval.Function(fn, [BigEval.Eval(argExpr, 200)], 200); }
        catch (MathException) { return; }
        var b = new System.Numerics.Complex(big.Re.ToDouble(), big.Im.ToDouble());
        Assert.True(System.Numerics.Complex.Abs(b - machine) < 1e-10 * (1 + System.Numerics.Complex.Abs(machine)),
            $"{fn}({arg}): machine {machine}, arbitrary precision {b}");
    }

    [Theory]
    [InlineData("sqrt(2) + pi")]
    [InlineData("(1 + sqrt(5))/2")]
    [InlineData("e^2/3")]
    [InlineData("2^(1/3)")]
    [InlineData("3*pi/7")]
    [InlineData("22/7")]
    [InlineData("1 + sqrt(3)")]
    [InlineData("pi^2/6")]
    [InlineData("log(2) + 1")]
    [InlineData("2*zeta(3) + 1")]
    public void IdentifyRecoversConstants(string constant)
    {
        var k = new Kernel();
        var exact = k.EvalText(constant);
        var found = k.EvalText($"identify(N({constant}, 50))");
        Assert.DoesNotContain(found.Subexpressions(), z => z is Real);
        var diff = BigEval.Eval(exact - found, 300);
        Assert.True(diff.Abs(300).IsZero || diff.Abs(300).Log2Magnitude < -250, $"identify gave {found} for {constant}");
    }

    [Fact]
    public void PrecisionIsTrackedThroughArithmetic()
    {
        var k = new Kernel();
        k.EvalText("a := N(pi, 30)");
        var sum = (Real)k.EvalText("a + 1");
        Assert.Equal(30, sum.Digits);
        var lower = (Real)k.EvalText("a * N(e, 20)");
        Assert.Equal(20, lower.Digits);
        var machine = (Real)k.EvalText("a + 0.5");
        Assert.True(machine.IsMachine);
        var sq = (Real)k.EvalText("sqrt(a)");
        Assert.Equal(30, sq.Digits);
        Assert.Equal("1.77245385090551602729816748334", TextFormatter.Format(sq));
    }

    [Fact]
    public void LongLiteralsKeepTheirDigits()
    {
        Assert.Equal("4.14159265358979323846264338327950288", Run("3.14159265358979323846264338327950288 + 1"));
        var r = (Real)new Kernel().EvalText("1.234567890123456789012345");
        Assert.Equal(25, r.Digits);
    }

    [Theory]
    [InlineData("(1 - cos(x))/x^2", "0", "1/2")]
    [InlineData("(cos(x))^(1/x^2)", "0", "e^(-1/2)")]
    [InlineData("(2^x - 1)/x", "0", "log(2)")]
    [InlineData("x*gamma(x)", "0", "1")]
    [InlineData("(1 + 1/n)^(n^2)*exp(-n)", "oo", "e^(-1/2)")]
    [InlineData("(x^x - 1)/(x*log(x))", "0", "1")]
    public void LimitsNeedingPrecision(string f, string point, string expected)
    {
        var v = f.Contains("/n") || f.Contains("(n") ? "n" : "x";
        var got = new Kernel().EvalText(point == "oo" ? $"limit({f}, {v}, {point})" : $"limit({f}, {v}, {point}, \"+\")");
        var want = new Kernel().EvalText(expected);
        Assert.DoesNotContain(got.Subexpressions(), z => z is Real);
        Assert.True(System.Numerics.Complex.Abs(NumericEval.Eval(got) - NumericEval.Eval(want)) < 1e-12, $"limit of {f}: {got}");
    }
}
