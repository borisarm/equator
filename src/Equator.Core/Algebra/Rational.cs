namespace Equator.Core.Algebra;

/// <summary>Rational-function manipulation: numerator/denominator split, together, cancel, apart.</summary>
public static class Rational
{
    /// <summary>Splits e into numerator and denominator over a common denominator.</summary>
    public static (Expr Num, Expr Den) NumDen(Expr e)
    {
        switch (e)
        {
            case Num n:
                return (new Num(n.Value.Num), new Num(n.Value.Den));
            case MulExpr m:
                {
                    var nums = new List<Expr>();
                    var dens = new List<Expr>();
                    foreach (var f in m.Factors)
                    {
                        var (a, b) = NumDen(f);
                        nums.Add(a);
                        dens.Add(b);
                    }
                    return (Expr.Times(nums), Expr.Times(dens));
                }
            case PowExpr p when p.Exponent is Num { Value.IsInteger: true } k:
                {
                    var (a, b) = NumDen(p.Base);
                    if (k.Value.Sign < 0)
                    {
                        var kk = new Num(-k.Value);
                        return (Expr.Power(b, kk), Expr.Power(a, kk));
                    }
                    return (Expr.Power(a, k), Expr.Power(b, k));
                }
            case PowExpr p when p.Exponent.HasNegativeCoefficient:
                return (Expr.One, Expr.Power(p.Base, -p.Exponent));
            case AddExpr a:
                return AddFractions(a.Terms.Select(NumDen).ToList());
        }
        return (e, Expr.One);
    }

    /// <summary>Adds fractions using the least common multiple of the (factored) denominators.</summary>
    private static (Expr, Expr) AddFractions(List<(Expr Num, Expr Den)> parts)
    {
        if (parts.All(p => p.Den.IsOne)) return (Expr.Plus(parts.Select(p => p.Num)), Expr.One);

        // Represent each denominator as base -> exponent and take the maximum exponent per base.
        static Dictionary<Expr, Expr> Powers(Expr d)
        {
            var map = new Dictionary<Expr, Expr>();
            foreach (var f in d is MulExpr m ? m.Factors : [d])
            {
                var (b, e) = f is PowExpr p ? (p.Base, p.Exponent) : (f, Expr.One);
                if (b.IsNumber) { b = f; e = Expr.One; }
                map[b] = map.TryGetValue(b, out var old) ? MaxExp(old, e) : e;
            }
            return map;
        }

        static Expr MaxExp(Expr a, Expr b)
        {
            var d = a - b;
            if (d.IsNumber) return d.IsNegativeNumber ? b : a;
            return a + b;
        }

        var lcmMap = new Dictionary<Expr, Expr>();
        var order = new List<Expr>();
        var numericLcm = System.Numerics.BigInteger.One;
        foreach (var (_, den) in parts)
        {
            var (c, rest) = den.SplitCoefficient();
            if (c is Num cn)
            {
                var v = System.Numerics.BigInteger.Abs(cn.Value.Num);
                numericLcm = numericLcm / System.Numerics.BigInteger.GreatestCommonDivisor(numericLcm, v) * v;
            }
            foreach (var (b, ex) in Powers(rest))
            {
                if (b.IsOne) continue;
                if (!lcmMap.TryGetValue(b, out var old)) { order.Add(b); lcmMap[b] = ex; }
                else lcmMap[b] = MaxExp(old, ex);
            }
        }
        var lcm = Expr.Times(order.Select(b => Expr.Power(b, lcmMap[b])).Prepend(new Num(numericLcm)));
        var numerator = Expr.Plus(parts.Select(p => p.Num * (lcm / p.Den)));
        return (numerator, lcm);
    }

    /// <summary>Combines a sum of fractions into one fraction with an expanded numerator.</summary>
    public static Expr Together(Expr e)
    {
        if (e is ListExpr or EqExpr) return e.WithArgs(e.Args.Select(Together).ToArray());
        e = e.MapBottomUp(x => x is FnExpr ? x.WithArgs(x.Args.Select(Together).ToArray()) : x);
        var (n, d) = NumDen(e);
        n = Expansion.Expand(n);
        if (d.IsOne) return n;
        if (n.IsZero) return Expr.Zero;
        return n / d;
    }

    /// <summary>Puts e over a common denominator and cancels common polynomial factors.</summary>
    public static Expr Cancel(Expr e)
    {
        if (e is ListExpr or EqExpr) return e.WithArgs(e.Args.Select(Cancel).ToArray());
        var (n, d) = NumDen(e);
        n = Expansion.Expand(n);
        d = Expansion.Expand(d);
        if (d.IsOne || n.IsZero) return n;
        if (d.IsNumber) return n / d;

        var vars = n.FreeSymbols().Union(d.FreeSymbols()).ToList();
        if (vars.Count == 1)
        {
            var x = vars[0];
            var pn = PolyView.ToQPoly(n, x);
            var pd = PolyView.ToQPoly(d, x);
            if (pn is not null && pd is not null)
            {
                var g = QPoly.Gcd(pn, pd);
                if (g.Degree > 0)
                {
                    pn = pn.DivRem(g).Quotient;
                    pd = pd.DivRem(g).Quotient;
                }
                // Normalise: monic-ish denominator with integer coefficients.
                var (cd, primD) = pd.ContentPrimitive();
                var num = (BigRational.One / cd) * pn;
                var (cn, primN) = num.ContentPrimitive();
                return new Num(cn) * primN.ToExpr(x) / primD.ToExpr(x);
            }
        }
        // Multivariate (or non-polynomial): cancel common factors structurally after factoring.
        var fn = Factoring.Factor(n);
        var fd = Factoring.Factor(d);
        var q = fn / fd;
        var (n2, d2) = NumDen(q);
        return Expansion.Expand(n2) / Expansion.Expand(d2);
    }

    /// <summary>Partial fraction decomposition with respect to x (rational coefficients only).</summary>
    public static Expr Apart(Expr e, Expr x)
    {
        var (n, d) = NumDen(Together(e));
        var pn = PolyView.ToQPoly(n, x);
        var pd = PolyView.ToQPoly(d, x);
        if (pn is null || pd is null || pd.Degree < 1) return e;
        var result = PartialFractions(pn, pd, x);
        return result ?? e;
    }

    /// <summary>
    /// Decomposes pn/pd into polynomial part plus sum of A(x)/f(x)^k over the factors of pd.
    /// Unknown numerator coefficients are found by solving a linear system.
    /// </summary>
    public static Expr? PartialFractions(QPoly pn, QPoly pd, Expr x)
    {
        var (quot, rem) = pn.DivRem(pd);
        var (content, factors) = pd.Factor();
        if (factors.Count == 0) return null;
        if (factors.Count == 1 && factors[0].Multiplicity == 1) return quot.ToExpr(x) + rem.ToExpr(x) / pd.ToExpr(x);

        // Build ansatz terms: for each factor f^m and each k=1..m, a numerator of degree deg(f)-1.
        var basis = new List<(QPoly Multiplier, int FactorIndex, int Power, int CoeffDegree)>();
        var full = pd;
        for (int fi = 0; fi < factors.Count; fi++)
        {
            var (f, m) = factors[fi];
            for (int k = 1; k <= m; k++)
            {
                // multiplier = pd / f^k
                var fk = QPoly.Constant(BigRational.One);
                for (int j = 0; j < k; j++) fk *= f;
                var mult = full.DivRem(fk).Quotient;
                for (int c = 0; c < f.Degree; c++) basis.Add((mult * QPoly.Monomial(BigRational.One, c), fi, k, c));
            }
        }
        int n = pd.Degree;
        if (basis.Count != n) return null;
        // Solve sum_j a_j * basis_j(x) = rem(x): n equations (coefficients of x^0..x^(n-1)).
        var m2 = new BigRational[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m2[i, j] = basis[j].Multiplier[i];
            m2[i, n] = rem[i];
        }
        var sol = LinearAlgebra.SolveRational(m2, n);
        if (sol is null) return null;
        var terms = new List<Expr> { quot.ToExpr(x) };
        var grouped = new Dictionary<(int, int), List<Expr>>();
        for (int j = 0; j < n; j++)
        {
            var (_, fi, k, c) = basis[j];
            if (!grouped.TryGetValue((fi, k), out var l)) grouped[(fi, k)] = l = [];
            l.Add(new Num(sol[j]) * Expr.Power(x, c));
        }
        foreach (var ((fi, k), nums) in grouped)
        {
            var numer = Expr.Plus(nums);
            if (numer.IsZero) continue;
            terms.Add(numer / Expr.Power(factors[fi].Factor.ToExpr(x), k));
        }
        return Expr.Plus(terms);
    }
}
