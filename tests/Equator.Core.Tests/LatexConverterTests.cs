using Equator.Core.Parsing;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

public class LatexConverterTests
{
    /// <summary>The converted LaTeX must evaluate to the same thing as the expected Equator input.</summary>
    [Theory]
    [InlineData(@"x ^ { 2 } + 2 x + 1", "x^2 + 2x + 1")]
    [InlineData(@"\frac { 1 } { 2 } x", "x/2")]
    [InlineData(@"\frac{a+b}{c}", "(a + b)/c")]
    [InlineData(@"\sqrt { x + 1 }", "sqrt(x + 1)")]
    [InlineData(@"\sqrt [ 3 ] { x }", "x^(1/3)")]
    [InlineData(@"2 \cdot 3 \times 4", "24")]
    [InlineData(@"\sin x \cos x", "sin(x)*cos(x)")]
    [InlineData(@"\sin 2 x", "sin(2x)")]
    [InlineData(@"\sin ^ { 2 } x + \cos ^ { 2 } x", "sin(x)^2 + cos(x)^2")]
    [InlineData(@"\sin ^ { - 1 } x", "asin(x)")]
    [InlineData(@"\ln ( x + 1 )", "log(x + 1)")]
    [InlineData(@"\log _ { 2 } 8", "log(8)/log(2)")]
    [InlineData(@"e ^ { - x ^ { 2 } }", "exp(-x^2)")]
    [InlineData(@"\left( x + 1 \right) ^ { 2 }", "(x + 1)^2")]
    [InlineData(@"\left| x - 1 \right|", "abs(x - 1)")]
    [InlineData(@"| x |", "abs(x)")]
    [InlineData(@"x y z", "x*y*z")]
    [InlineData(@"1 8 5", "185")]
    [InlineData(@"3.5 x", "3.5*x")]
    [InlineData(@"\pi r ^ { 2 }", "pi*r^2")]
    [InlineData(@"\int x e ^ { x } d x", "integrate(x*exp(x), x)")]
    [InlineData(@"\int _ { 0 } ^ { 1 } x ^ { 2 } \, d x", "integrate(x^2, x, 0, 1)")]
    [InlineData(@"\int \frac { d x } { x }", "integrate(1/x, x)")]
    [InlineData(@"\int _ { t _ { 1 } } ^ { t _ { 2 } } v d t", "integrate(v, t, t_1, t_2)")]
    [InlineData(@"\frac { d } { d x } ( x ^ { 2 } \sin x )", "diff(x^2 sin(x), x)")]
    [InlineData(@"\frac { d ^ { 2 } } { d x ^ { 2 } } x ^ { 4 }", "diff(x^4, x, 2)")]
    [InlineData(@"\lim _ { x \to 0 } \frac { \sin x } { x }", "limit(sin(x)/x, x, 0)")]
    [InlineData(@"\lim _ { x \rightarrow \frac { 1 } { 4 } } \frac { 1 - 4 ^ { x - \frac { 1 } { 4 } } } { 1 - 4 x }", "limit((1 - 4^(x - 1/4))/(1 - 4x), x, 1/4)")]
    [InlineData(@"\sum _ { k = 1 } ^ { n } k ^ { 2 }", "sum(k^2, k, 1, n)")]
    [InlineData(@"\sum _ { n = 1 } ^ { \infty } \frac { 1 } { n ^ { 2 } }", "pi^2/6")]
    [InlineData(@"n !", "n!")]
    [InlineData(@"\binom { 5 } { 2 }", "10")]
    [InlineData(@"90 ^ { \circ }", "pi/2")]
    [InlineData(@"\frac { 1 } { 2 } \cdot \frac { 2 } { 3 } .", "1/3")]
    [InlineData(@"\alpha + \beta", "alpha + beta")]
    [InlineData(@"x _ { 1 } + x _ { 2 }", "x_1 + x_2")]
    [InlineData(@"f ( x ) = x ^ { 2 }", "f(x) = x^2")]
    [InlineData(@"\begin{pmatrix} 1 & 2 \\ 3 & 4 \end{pmatrix}", "[[1, 2], [3, 4]]")]
    public void ConvertsToEquivalentInput(string latex, string expected)
    {
        var lines = LatexConverter.Convert(latex);
        Assert.Single(lines);
        var got = new Kernel().EvalText(lines[0]);
        var want = new Kernel().EvalText(expected);
        Assert.True(got == want || Algebra.Expansion.Expand(got - want).IsZero,
            $"{latex} → {lines[0]} = {TextFormatter.Format(got)}, expected {TextFormatter.Format(want)}");
    }

    [Theory]
    [InlineData(@"x + 1 = 3 \\ x = 2", 2)]
    [InlineData(@"x + 1 = 3 \Rightarrow x = 2", 2)]
    [InlineData(@"\begin{aligned} 2 x + 3 & = 7 \\ 2 x & = 4 \\ x & = 2 \end{aligned}", 3)]
    [InlineData(@"\left\{ \begin{matrix} \frac { d y } { d x } + y = 3 \\ \frac { d ^ { 2 } y } { d x ^ { 2 } } + 3 ( \frac { d y } { d x } ) ^ { 3 } + y = 0 . \end{matrix} \right.", 2)]
    [InlineData(@"\left\{ \begin{array} { l } x + y = 3 \\ x - y = 1 \end{array} \right.", 2)]
    public void StackedRowsBecomeLines(string latex, int count)
    {
        var lines = LatexConverter.Convert(latex);
        Assert.Equal(count, lines.Count);
        foreach (var l in lines) new Kernel().EvalText(l); // each line parses and evaluates
    }

    [Fact]
    public void DerivativeNotationBecomesPrimes()
    {
        var lines = LatexConverter.Convert(@"\frac { d y } { d x } + y = 3");
        Assert.Equal("y' + y = 3", lines[0]);
        Assert.Equal("y'' + y = 0", LatexConverter.Convert(@"\frac { d ^ { 2 } y } { d x ^ { 2 } } + y = 0")[0]);
    }

    [Theory]
    [InlineData(@"x = 2 \text { or } x = 3", "x = 2 or x = 3")]
    [InlineData(@"x = 2 , x = - 3", "x = 2 , x = -3")]
    [InlineData(@"x = \pm 3", "x = ±3")]
    [InlineData(@"x = 2 o r x = 3", "x = 2 or x = 3")]
    [InlineData(@"x _ { 1 } = 2 a n d x _ { 2 } = 3", "x_1 = 2 and x_2 = 3")]
    [InlineData(@"( x - 2 ) ( x - 3 ) = 0", "(x - 2)(x - 3) = 0")]
    public void SeparatorsArePreserved(string latex, string expected) =>
        Assert.Equal(expected, LatexConverter.Convert(latex)[0].Replace("  ", " "));
}
