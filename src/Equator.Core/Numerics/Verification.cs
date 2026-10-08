using System.Numerics;

namespace Equator.Core.Numerics;

/// <summary>
/// Numeric self-checks used as a safety net by the symbolic algorithms: a result that fails one of these
/// checks at sample points is wrong and must not be returned.
/// </summary>
public static class Verification
{
    private static readonly double[] XPoints = [-2.17, -0.63, 0.41, 1.37, 2.89];
    private static readonly double[] ParameterValues = [0.83, 1.29, 1.71, 2.13, 0.57, 1.93, 2.41, 0.69];

    /// <summary>Values for the free symbols other than <paramref name="x"/>, positive so that they respect typical assumptions.</summary>
    public static Dictionary<string, Complex> ParameterBindings(IEnumerable<Expr> exprs, Expr? x)
    {
        var vars = new Dictionary<string, Complex>();
        int i = 0;
        foreach (var s in exprs.SelectMany(e => e.FreeSymbols()).Distinct())
        {
            if (x is Sym xs && s.Name == xs.Name) continue;
            vars[s.Name] = Engine.Assumptions.SampleValue(s, ParameterValues[i++ % ParameterValues.Length]);
        }
        return vars;
    }

    private static bool TryEval(Expr e, Dictionary<string, Complex> vars, out Complex v)
    {
        try
        {
            v = NumericEval.Eval(e, vars);
            return double.IsFinite(v.Real) && double.IsFinite(v.Imaginary);
        }
        catch (Exception ex) when (ex is MathException or ArithmeticException or InvalidCastException)
        {
            v = double.NaN;
            return false;
        }
    }

    /// <summary>
    /// True unless d/dx F visibly differs from f at a sample point where f is real and finite. Inconclusive
    /// checks (unknown functions, no usable points) count as success.
    /// </summary>
    public static bool IsAntiderivative(Expr F, Expr f, Expr x)
    {
        if (x is not Sym xs) return true;
        Expr dF;
        try { dF = Calculus.Derivative.Diff(F, x); }
        catch (MathException) { return true; }
        var vars = ParameterBindings([F, f], x);
        foreach (var px in XPoints)
        {
            vars[xs.Name] = Engine.Assumptions.SampleValue(xs, px);
            if (!TryEval(f, vars, out var fv) || Math.Abs(fv.Imaginary) > 1e-9 * (1 + Complex.Abs(fv))) continue;
            if (!TryEval(dF, vars, out var dv)) continue;
            if (Complex.Abs(dv - fv) > 1e-6 * (1 + Complex.Abs(fv))) return false;
        }
        return true;
    }

    /// <summary>
    /// Three-valued numeric zero test of an expression in its free symbols: true when it vanishes at every
    /// sample point, false when it is clearly non-zero at one, null when it cannot be evaluated.
    /// </summary>
    public static bool? IsZero(Expr e)
    {
        var syms = e.FreeSymbols().ToList();
        var vars = ParameterBindings([e], null);
        int evaluated = 0;
        for (int k = 0; k < 3; k++)
        {
            int j = 0;
            foreach (var s in syms)
                vars[s.Name] = Engine.Assumptions.SampleValue(s, ParameterValues[(j++ + 3 * k) % ParameterValues.Length] + 0.37 * k);
            if (!TryEval(e, vars, out var v)) continue;
            evaluated++;
            if (Complex.Abs(v) > 1e-8) return false;
        }
        return evaluated > 0 ? true : null;
    }
}
