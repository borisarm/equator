using System.Numerics;

namespace Equator.Core.Algebra;

public static class Factoring
{
    /// <summary>Factors polynomials and rational functions over the rationals (numerator and denominator separately).</summary>
    public static Expr Factor(Expr e)
    {
        if (e is ListExpr or EqExpr) return e.WithArgs(e.Args.Select(Factor).ToArray());
        if (e.IsNumber || e is Sym) return e;
        var (n, d) = Rational.NumDen(e);
        var fn = FactorPolynomial(n);
        if (d.IsOne) return fn;
        return fn / FactorPolynomial(d);
    }

    /// <summary>
    /// Complete factorisation over Q of a polynomial in any generators (symbols, function calls, radicals,
    /// exponentials; see <see cref="PolyRing"/>).
    /// </summary>
    public static Expr FactorPolynomial(Expr p)
    {
        p = Expansion.Expand(p);
        if (p is not AddExpr) return p;
        var ring = PolyRing.For([p]);
        var poly = ring?.ToPoly(p);
        if (ring is null || poly is null) return FactorContent(p);
        var (unit, factors) = PolyFactor.Factor(poly);
        return Expr.Times(factors.Select(f => Expr.Power(ring.ToExpr(f.Factor), f.Multiplicity)).Prepend(new Num(unit)));
    }

    /// <summary>Fallback for non-polynomial sums (e.g. floating-point coefficients): pull out the numeric content and common monomial.</summary>
    private static Expr FactorContent(Expr p)
    {
        var terms = ((AddExpr)p).Terms;
        var coeffs = terms.Select(t => t.SplitCoefficient().Coeff).ToList();
        BigRational content = BigRational.One;
        if (coeffs.All(c => c is Num))
        {
            BigInteger g = 0, l = 1;
            foreach (var c in coeffs.Cast<Num>())
            {
                g = BigInteger.GreatestCommonDivisor(g, c.Value.Num);
                l = l / BigInteger.GreatestCommonDivisor(l, c.Value.Den) * c.Value.Den;
            }
            content = new BigRational(g, l);
        }
        var monomial = new List<Expr>();
        var first = PowerMap(terms[0]);
        foreach (var (b, e0) in first)
        {
            var minExp = e0;
            bool everywhere = true;
            foreach (var t in terms.Skip(1))
            {
                if (!PowerMap(t).TryGetValue(b, out var et)) { everywhere = false; break; }
                minExp = BigInteger.Min(minExp, et);
            }
            if (everywhere && minExp > 0) monomial.Add(Expr.Power(b, new Num(minExp)));
        }
        var common = Expr.Times(monomial.Prepend(new Num(content)));
        if (common.IsOne) return p;
        return Expr.Times(common, Expansion.Expand(p / common));
    }

    private static Dictionary<Expr, BigInteger> PowerMap(Expr term)
    {
        var map = new Dictionary<Expr, BigInteger>();
        foreach (var f in term is MulExpr m ? m.Factors : [term])
        {
            if (f.IsNumber) continue;
            if (f is PowExpr p && p.Exponent is Num { Value.IsInteger: true } n) map[p.Base] = n.Value.Num;
            else map[f] = 1;
        }
        return map;
    }

    /// <summary>Square-free factorisation: Π fᵢ^i with the fᵢ square-free and pairwise coprime.</summary>
    public static Expr SquareFreeFactor(Expr e)
    {
        var (n, d) = Rational.NumDen(Rational.Together(e));
        return SquareFreePolynomial(n) / SquareFreePolynomial(d);
    }

    private static Expr SquareFreePolynomial(Expr p)
    {
        p = Expansion.Expand(p);
        if (p is not AddExpr) return p;
        var ring = PolyRing.For([p]);
        var poly = ring?.ToPoly(p);
        if (ring is null || poly is null) return p;
        var parts = SquareFreeParts(poly.Normalized());
        // Combine parts of equal multiplicity (from the content and the primitive part).
        var byMult = parts.GroupBy(t => t.Multiplicity).OrderBy(g => g.Key)
            .Select(g => (Factor: g.Aggregate(MPoly.Constant(poly.NVars, BigRational.One), (acc, t) => acc * t.Factor), Mult: g.Key))
            .ToList();
        var product = MPoly.Constant(poly.NVars, BigRational.One);
        foreach (var (f, m) in byMult) product *= f.Pow(m);
        var unit = poly.Lead.C / product.Lead.C;
        return Expr.Times(byMult.Select(t => Expr.Power(ring.ToExpr(t.Factor), t.Mult)).Prepend(new Num(unit)));
    }

    private static List<(MPoly Factor, int Multiplicity)> SquareFreeParts(MPoly p)
    {
        if (p.IsConstant) return [];
        int v = p.Variables.First();
        var content = PolyGcd.Content(p, v);
        var prim = MPoly.TryDivide(p, content)!.Normalized();
        var parts = PolyGcd.SquareFree(prim, v);
        parts.AddRange(SquareFreeParts(content.Normalized()));
        return parts;
    }

    /// <summary>Greatest common divisor of polynomials (any generators); null when an argument is not a polynomial.</summary>
    public static Expr? PolynomialGcd(IReadOnlyList<Expr> items, bool lcm = false)
    {
        var expanded = items.Select(Expansion.Expand).ToList();
        var ring = PolyRing.For(expanded);
        if (ring is null) return null;
        var polys = expanded.Select(ring.ToPoly).ToList();
        if (polys.Any(q => q is null)) return null;
        var acc = polys[0]!;
        foreach (var q in polys.Skip(1)) acc = lcm ? PolyGcd.Lcm(acc, q!) : PolyGcd.Gcd(acc, q!);
        return Factor(ring.ToExpr(acc.Normalized()));
    }

    /// <summary>Integer factorisation as a list of [prime, exponent] pairs.</summary>
    public static Expr FactorInteger(BigInteger n)
    {
        var items = new List<Expr>();
        if (n.Sign < 0) items.Add(Expr.List(Expr.MinusOne, Expr.One));
        foreach (var (p, e) in IntMath.Factor(n))
            items.Add(Expr.List(new Num(p), new Num(e)));
        return Expr.List(items);
    }
}
