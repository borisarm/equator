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

    public static Expr FactorPolynomial(Expr p)
    {
        p = Expansion.Expand(p);
        if (p is not AddExpr sum) return p;
        var terms = sum.Terms;

        // 1. numeric content (only when every coefficient is rational)
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

        // 2. common monomial factor: bases present in every term, with the minimum integer exponent
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
        var rest = Expansion.Expand(p / common);

        // Make the leading term positive so that equal factors compare equal.
        if (LeadingTerm(rest).HasNegativeCoefficient)
        {
            common = -common;
            rest = Expansion.Expand(-rest);
        }

        var factored = FactorPrimitive(rest);
        return Expr.Times(common, factored);
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

    private static Expr LeadingTerm(Expr p)
    {
        if (p is not AddExpr a) return p;
        // The term printed first: highest total degree.
        return a.Terms.OrderByDescending(TotalDegree).First();
    }

    private static double TotalDegree(Expr t) =>
        (t is MulExpr m ? m.Factors : [t]).Sum(f => f switch
        {
            _ when f.IsNumber => 0,
            PowExpr { Exponent: Num n } => n.Value.ToDouble(),
            _ => 1,
        });

    /// <summary>Factors a polynomial with no numeric content or monomial factor.</summary>
    private static Expr FactorPrimitive(Expr p)
    {
        if (p is not AddExpr) return p;
        var kernels = Kernels(p);
        if (kernels is null || kernels.Count == 0) return p;

        // Replace each kernel (symbol, function call, ...) by a fresh symbol.
        var temps = new Dictionary<Expr, Expr>();
        var back = new Dictionary<Expr, Expr>();
        for (int i = 0; i < kernels.Count; i++)
        {
            var t = new Sym("\u0001k" + i);
            temps[kernels[i]] = t;
            back[t] = kernels[i];
        }
        bool allSymbols = kernels.All(k => k is Sym);
        var q = allSymbols ? p : p.Substitute(temps);
        var vars = allSymbols ? kernels : kernels.Select(k => temps[k]).ToList();

        Expr? result = null;
        if (vars.Count == 1)
            result = FactorUnivariate(q, vars[0]);
        else if (vars.Count == 2)
            result = FactorHomogeneous(q, vars[0], vars[1]) ?? FactorByVariable(q, vars);
        else
            result = FactorByVariable(q, vars);

        if (result is null) return p;
        return allSymbols ? result : result.Substitute(back);
    }

    /// <summary>Distinct generators of a polynomial: the bases appearing with positive integer exponents.</summary>
    private static List<Expr>? Kernels(Expr p)
    {
        var set = new List<Expr>();
        foreach (var t in ((AddExpr)p).Terms)
        {
            foreach (var f in t is MulExpr m ? m.Factors : [t])
            {
                if (f.IsNumber) continue;
                Expr b = f;
                if (f is PowExpr pw)
                {
                    if (pw.Exponent is not Num { Value.IsInteger: true } n || n.Value.Sign < 0) return null;
                    b = pw.Base;
                }
                if (b is AddExpr) return null;
                if (!set.Contains(b)) set.Add(b);
            }
        }
        set.Sort(Expr.Compare);
        return set;
    }

    private static Expr? FactorUnivariate(Expr p, Expr x)
    {
        var poly = PolyView.ToQPoly(p, x);
        if (poly is null || poly.Degree < 2) return null;
        var (content, factors) = poly.Factor();
        if (factors.Count == 1 && factors[0].Multiplicity == 1) return null;
        return Expr.Times(factors.Select(f => Expr.Power(f.Factor.ToExpr(x), f.Multiplicity)).Prepend(new Num(content)));
    }

    /// <summary>Homogeneous polynomials in x, y: factor p(x, 1) and homogenise each factor.</summary>
    private static Expr? FactorHomogeneous(Expr p, Expr x, Expr y)
    {
        var terms = ((AddExpr)p).Terms;
        int? degree = null;
        foreach (var t in terms)
        {
            var map = PowerMap(t);
            var d = map.Values.Aggregate(BigInteger.Zero, (a, b) => a + b);
            if (degree is null) degree = (int)d;
            else if (degree != (int)d) return null;
        }
        var uni = PolyView.ToQPoly(p.Substitute(y, Expr.One), x);
        if (uni is null) return null;
        var (content, factors) = uni.Factor();
        if (factors.Count == 1 && factors[0].Multiplicity == 1) return null;
        var parts = new List<Expr> { new Num(content) };
        int used = 0;
        foreach (var (f, m) in factors)
        {
            var k = f.Degree;
            var hom = Expr.Plus(f.Coeffs.Select((c, i) => new Num(c) * Expr.Power(x, i) * Expr.Power(y, k - i)));
            parts.Add(Expr.Power(hom, m));
            used += k * m;
        }
        if (degree!.Value > used) parts.Add(Expr.Power(y, degree.Value - used));
        return Expr.Times(parts);
    }

    /// <summary>
    /// Fallback for multivariate polynomials: look for a variable in which p is linear or quadratic with
    /// coefficients whose structure lets us split p, e.g. factoring by grouping a*x + a*y + b*x + b*y.
    /// </summary>
    private static Expr? FactorByVariable(Expr p, List<Expr> vars)
    {
        foreach (var x in vars)
        {
            var c = PolyView.Coefficients(p, x);
            if (c is null || c.Count < 2) continue;
            // Common factor of all coefficients (as polynomials in the other variables).
            var factoredCoeffs = c.Select(FactorPolynomial).ToList();
            var common = CommonFactors(factoredCoeffs.Where(k => !k.IsZero).ToList());
            if (common is not null && !common.IsOne && !common.IsNumber)
            {
                var rest = Expansion.Expand(p / common);
                return Expr.Times(FactorPolynomial(common), FactorPolynomial(rest));
            }
        }
        return null;
    }

    private static Expr? CommonFactors(List<Expr> items)
    {
        if (items.Count == 0) return null;
        Dictionary<Expr, BigInteger> Map(Expr e) => PowerMap(e);
        var first = Map(items[0]);
        var result = new List<Expr>();
        foreach (var (b, e) in first)
        {
            var min = e;
            bool all = true;
            foreach (var other in items.Skip(1))
            {
                if (!Map(other).TryGetValue(b, out var oe)) { all = false; break; }
                min = BigInteger.Min(min, oe);
            }
            if (all) result.Add(Expr.Power(b, new Num(min)));
        }
        return result.Count == 0 ? null : Expr.Times(result);
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
