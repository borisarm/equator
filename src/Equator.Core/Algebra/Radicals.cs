using System.Numerics;

namespace Equator.Core.Algebra;

/// <summary>Simplification of nested square roots.</summary>
public static class Radicals
{
    /// <summary>
    /// Denests sqrt(p + q sqrt(r)) = sqrt(x) + sign(q) sqrt(y) when x + y = p and 4 x y = q² r have rational
    /// solutions x ≥ y ≥ 0 (e.g. sqrt(3 + 2 sqrt(2)) = 1 + sqrt(2)). Applied bottom-up; anything else is unchanged.
    /// </summary>
    public static Expr Denest(Expr e) => e.MapBottomUp(z => z is PowExpr { Exponent: Num { Value: var h } } pw && h == BigRational.Half ? DenestSqrt(pw.Base) ?? z : z);

    private static Expr? DenestSqrt(Expr radicand)
    {
        if (radicand is not AddExpr sum || sum.Terms.Count != 2) return null;
        BigRational? p = null;
        BigRational q = BigRational.Zero, r = BigRational.Zero;
        foreach (var t in sum.Terms)
        {
            if (t is Num n) { p = n.Value; continue; }
            var (c, rest) = t.SplitCoefficient();
            if (c is not Num cn || rest is not PowExpr { Base: Num rb, Exponent: Num { Value: var half } } || half != BigRational.Half) return null;
            q = cn.Value; r = rb.Value;
        }
        if (p is null || q.IsZero) return null;
        // x, y = (p ± sqrt(p² − q² r)) / 2
        var disc = p.Value * p.Value - q * q * r;
        if (disc.Sign < 0) return null;
        if (!IntMath.TryExactRoot(disc.Num, 2, out var sn) || !IntMath.TryExactRoot(disc.Den, 2, out var sd)) return null;
        var s = new BigRational(sn, sd);
        var x = (p.Value + s) / 2;
        var y = (p.Value - s) / 2;
        if (x.Sign < 0 || y.Sign < 0) return null;
        var result = Expr.Sqrt(new Num(x)) + (q.Sign > 0 ? Expr.One : Expr.MinusOne) * Expr.Sqrt(new Num(y));
        return result;
    }
}
