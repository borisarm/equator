namespace Equator.Core.Algebra;

/// <summary>
/// Greatest common divisors, contents and square-free decomposition of multivariate polynomials over Q,
/// by the primitive polynomial remainder sequence applied recursively in one variable at a time.
/// Results are normalised to integer coefficients with a positive leading coefficient.
/// </summary>
public static class PolyGcd
{
    public static MPoly Gcd(MPoly a, MPoly b)
    {
        Engine.Interrupt.Check();
        int n = a.NVars;
        if (a.IsZero) return b.Normalized();
        if (b.IsZero) return a.Normalized();
        if (a.IsConstant || b.IsConstant) return MPoly.Constant(n, BigRational.One);

        var vars = a.Variables.Union(b.Variables).OrderBy(v => v).ToList();
        if (vars.Count == 1 && a.Variables.Count() == 1 && b.Variables.Count() == 1)
        {
            int x = vars[0];
            var g = QPoly.Gcd(a.ToQPoly(x), b.ToQPoly(x));
            return MPoly.FromUnivariate(n, x, g.Coeffs).Normalized();
        }

        var heuristic = HeuristicGcd(a.Normalized(), b.Normalized());
        if (heuristic is not null) return heuristic.Normalized();
        return PrsGcd(a, b, vars);
    }

    /// <summary>
    /// GCDHEU (Char, Geddes, Gonnet): evaluate one variable at a large integer ξ, take the gcd of the images
    /// recursively, rebuild a candidate from the ξ-adic digits of the result and accept it if it divides both
    /// inputs. Fast for the small dense inputs typical here; null when it gives up.
    /// </summary>
    private static MPoly? HeuristicGcd(MPoly a, MPoly b)
    {
        Engine.Interrupt.Check();
        int n = a.NVars;
        if (a.IsZero) return b;
        if (b.IsZero) return a;
        // The integer contents are part of the gcd of the (non-primitive) images: keep them separately.
        var contentA = IntegerContent(a);
        var contentB = IntegerContent(b);
        var g0 = new BigRational(System.Numerics.BigInteger.GreatestCommonDivisor(contentA, contentB));
        if (a.IsConstant || b.IsConstant) return MPoly.Constant(n, g0);
        a = (BigRational.One / new BigRational(contentA)) * a;
        b = (BigRational.One / new BigRational(contentB)) * b;
        var vars = a.Variables.Union(b.Variables).ToList();
        int x = vars.Max();
        var maxA = a.Terms.Max(t => System.Numerics.BigInteger.Abs(t.C.Num));
        var maxB = b.Terms.Max(t => System.Numerics.BigInteger.Abs(t.C.Num));
        var xi = 2 * System.Numerics.BigInteger.Min(maxA, maxB) + 29;
        int degree = Math.Max(a.Degree(x), b.Degree(x));
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if ((long)xi.GetBitLength() * Math.Max(degree, 1) > 20000) return null;
            var ga = a.Evaluate(x, xi);
            var gb = b.Evaluate(x, xi);
            var gamma = HeuristicGcd(ga, gb);
            if (gamma is not null)
            {
                var candidate = Reconstruct(gamma, x, xi);
                candidate = (BigRational.One / new BigRational(IntegerContent(candidate))) * candidate;
                if (!candidate.IsZero && MPoly.TryDivide(a, candidate) is { } qa && qa.HasIntegerCoefficients
                    && MPoly.TryDivide(b, candidate) is { } qb && qb.HasIntegerCoefficients)
                    return g0 * candidate;
            }
            xi = xi * 73794 / 27011;
        }
        return null;
    }

    private static System.Numerics.BigInteger IntegerContent(MPoly p)
    {
        System.Numerics.BigInteger g = 0;
        foreach (var (_, c) in p.Terms) g = System.Numerics.BigInteger.GreatestCommonDivisor(g, c.Num);
        return g.IsZero ? 1 : g;
    }

    /// <summary>Σ gᵢ x^i where gamma = Σ gᵢ ξ^i with coefficients of gᵢ in the symmetric range (−ξ/2, ξ/2].</summary>
    private static MPoly Reconstruct(MPoly gamma, int x, System.Numerics.BigInteger xi)
    {
        var result = MPoly.Zero(gamma.NVars);
        var half = xi / 2;
        int i = 0;
        while (!gamma.IsZero && i < 10000)
        {
            var digit = gamma.MapCoefficients(c =>
            {
                var r = System.Numerics.BigInteger.Remainder(c.Num, xi);
                if (r.Sign < 0) r += xi;
                if (r > half) r -= xi;
                return new BigRational(r);
            });
            result += digit * MPoly.Var(gamma.NVars, x, i);
            gamma = (BigRational.One / new BigRational(xi)) * (gamma - digit);
            i++;
        }
        return result;
    }

    /// <summary>Primitive polynomial remainder sequence, recursively in one variable at a time.</summary>
    private static MPoly PrsGcd(MPoly a, MPoly b, List<int> vars)
    {
        int n = a.NVars;
        // Main variable: the one of least degree (cheapest remainder sequence).
        int v = vars.OrderBy(w => Math.Max(a.Degree(w), b.Degree(w))).First();
        if (!a.Contains(v)) return Gcd(a, Content(b, v));
        if (!b.Contains(v)) return Gcd(Content(a, v), b);

        var ca = Content(a, v);
        var cb = Content(b, v);
        var c = Gcd(ca, cb);
        var pa = MPoly.TryDivide(a, ca)!;
        var pb = MPoly.TryDivide(b, cb)!;
        if (pa.Degree(v) < pb.Degree(v)) (pa, pb) = (pb, pa);
        while (!pb.IsZero && pb.Degree(v) > 0)
        {
            var r = PseudoRemainder(pa, pb, v);
            pa = pb;
            pb = r.IsZero ? r : PrimitivePart(r, v);
        }
        var g2 = pb.IsZero ? PrimitivePart(pa, v) : MPoly.Constant(n, BigRational.One);
        return (g2 * c).Normalized();
    }

    public static MPoly Lcm(MPoly a, MPoly b)
    {
        if (a.IsZero || b.IsZero) return MPoly.Zero(a.NVars);
        var g = Gcd(a, b);
        return (MPoly.TryDivide(a, g)! * b).Normalized();
    }

    /// <summary>Gcd of the coefficients of p as a polynomial in v.</summary>
    public static MPoly Content(MPoly p, int v)
    {
        MPoly? g = null;
        foreach (var c in p.CoefficientsIn(v).Where(c => !c.IsZero).OrderBy(c => c.TermCount))
        {
            g = g is null ? c.Normalized() : Gcd(g, c);
            if (g.IsConstant) return MPoly.Constant(p.NVars, BigRational.One);
        }
        return g ?? MPoly.Constant(p.NVars, BigRational.One);
    }

    public static MPoly PrimitivePart(MPoly p, int v)
    {
        var c = Content(p, v);
        return MPoly.TryDivide(p, c)!.Normalized();
    }

    /// <summary>lc(b)^k a mod b in the variable v (successive leading-term elimination).</summary>
    public static MPoly PseudoRemainder(MPoly a, MPoly b, int v)
    {
        int db = b.Degree(v);
        var lcB = b.CoefficientIn(v, db);
        var r = a;
        while (!r.IsZero && r.Degree(v) >= db)
        {
            Engine.Interrupt.Check();
            int d = r.Degree(v);
            var lcR = r.CoefficientIn(v, d);
            r = lcB * r - lcR * MPoly.Var(a.NVars, v, d - db) * b;
        }
        return r;
    }

    /// <summary>
    /// Square-free decomposition of a polynomial that is primitive in v (Yun's algorithm): factors with
    /// multiplicities whose product, to the given powers, equals p up to a constant.
    /// </summary>
    public static List<(MPoly Factor, int Multiplicity)> SquareFree(MPoly p, int v)
    {
        var result = new List<(MPoly, int)>();
        if (p.Degree(v) < 1) return result;
        var dp = p.Derivative(v);
        var g = Gcd(p, dp);
        var b = MPoly.TryDivide(p, g)!;
        var c = MPoly.TryDivide(dp, g)!;
        var d = c - b.Derivative(v);
        int i = 1;
        while (b.Degree(v) > 0)
        {
            var a = Gcd(b, d);
            if (a.Degree(v) > 0) result.Add((a, i));
            b = MPoly.TryDivide(b, a)!;
            c = MPoly.TryDivide(d, a)!;
            d = c - b.Derivative(v);
            if (++i > 10000) break;
        }
        return result;
    }
}
