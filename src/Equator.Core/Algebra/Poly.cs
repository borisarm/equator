using System.Numerics;

namespace Equator.Core.Algebra;

/// <summary>Helpers for viewing expressions as polynomials in one variable with symbolic coefficients.</summary>
public static class PolyView
{
    public const int MaxDegree = 2000;

    /// <summary>
    /// Returns coefficients [c0, c1, ..., cn] such that e = sum ci x^i, with each ci free of x,
    /// or null when e is not a polynomial in x.
    /// </summary>
    public static List<Expr>? Coefficients(Expr e, Expr x)
    {
        e = Expansion.Expand(e);
        var coeffs = new Dictionary<int, List<Expr>>();
        int maxDeg = 0;
        foreach (var term in e is AddExpr a ? a.Terms : [e])
        {
            if (!TryTerm(term, x, out var deg, out var c)) return null;
            if (!coeffs.TryGetValue(deg, out var list)) coeffs[deg] = list = [];
            list.Add(c);
            maxDeg = Math.Max(maxDeg, deg);
        }
        var result = new List<Expr>();
        for (int i = 0; i <= maxDeg; i++)
            result.Add(coeffs.TryGetValue(i, out var l) ? Expr.Plus(l) : Expr.Zero);
        while (result.Count > 1 && result[^1].IsZero) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool TryTerm(Expr term, Expr x, out int degree, out Expr coeff)
    {
        degree = 0; coeff = term;
        if (term.FreeOf(x)) return true;
        var rest = new List<Expr>();
        foreach (var f in term is MulExpr m ? m.Factors : [term])
        {
            if (f.FreeOf(x)) { rest.Add(f); continue; }
            if (f == x) { degree += 1; continue; }
            if (f is PowExpr p && p.Base == x && p.Exponent is Num { Value.IsInteger: true } n && n.Value.Sign > 0 && n.Value.Num <= MaxDegree)
            {
                degree += (int)n.Value.Num;
                continue;
            }
            return false;
        }
        coeff = Expr.Times(rest);
        return true;
    }

    public static bool IsPolynomial(Expr e, Expr x) => Coefficients(e, x) is not null;

    public static int Degree(Expr e, Expr x) => Coefficients(e, x) is { } c ? c.Count - 1 : -1;

    public static Expr FromCoefficients(IReadOnlyList<Expr> coeffs, Expr x) =>
        Expr.Plus(coeffs.Select((c, i) => c * Expr.Power(x, i)));

    /// <summary>Tries to view e as a polynomial in x with rational coefficients.</summary>
    public static QPoly? ToQPoly(Expr e, Expr x)
    {
        var c = Coefficients(e, x);
        if (c is null || c.Any(k => k is not Num)) return null;
        return new QPoly(c.Select(k => ((Num)k).Value));
    }
}

/// <summary>Dense univariate polynomial with exact rational coefficients (index = power).</summary>
public sealed class QPoly
{
    private readonly BigRational[] _c;

    public QPoly(IEnumerable<BigRational> coeffs)
    {
        var list = coeffs.ToList();
        while (list.Count > 0 && list[^1].IsZero) list.RemoveAt(list.Count - 1);
        _c = list.ToArray();
    }

    public static QPoly Zero { get; } = new([]);
    public static QPoly Constant(BigRational c) => new([c]);
    public static QPoly Monomial(BigRational c, int degree) => new(Enumerable.Repeat(BigRational.Zero, degree).Append(c));

    public int Degree => _c.Length - 1;
    public bool IsZero => _c.Length == 0;
    public BigRational this[int i] => i < _c.Length && i >= 0 ? _c[i] : BigRational.Zero;
    public BigRational Lead => IsZero ? BigRational.Zero : _c[^1];
    public IReadOnlyList<BigRational> Coeffs => _c;

    public static QPoly operator +(QPoly a, QPoly b)
    {
        int n = Math.Max(a._c.Length, b._c.Length);
        return new(Enumerable.Range(0, n).Select(i => a[i] + b[i]));
    }

    public static QPoly operator -(QPoly a, QPoly b)
    {
        int n = Math.Max(a._c.Length, b._c.Length);
        return new(Enumerable.Range(0, n).Select(i => a[i] - b[i]));
    }

    public static QPoly operator *(QPoly a, QPoly b)
    {
        if (a.IsZero || b.IsZero) return Zero;
        var r = new BigRational[a._c.Length + b._c.Length - 1];
        for (int i = 0; i < r.Length; i++) r[i] = BigRational.Zero;
        for (int i = 0; i < a._c.Length; i++)
        {
            if (a._c[i].IsZero) continue;
            for (int j = 0; j < b._c.Length; j++) r[i + j] += a._c[i] * b._c[j];
        }
        return new(r);
    }

    public static QPoly operator *(BigRational k, QPoly a) => new(a._c.Select(c => c * k));

    public (QPoly Quotient, QPoly Remainder) DivRem(QPoly d)
    {
        if (d.IsZero) throw new DivideByZeroException();
        var rem = _c.ToArray();
        int dq = Degree - d.Degree;
        if (dq < 0) return (Zero, this);
        var q = new BigRational[dq + 1];
        for (int i = 0; i <= dq; i++) q[i] = BigRational.Zero;
        for (int k = dq; k >= 0; k--)
        {
            var coef = rem[k + d.Degree] / d.Lead;
            q[k] = coef;
            if (coef.IsZero) continue;
            for (int j = 0; j <= d.Degree; j++) rem[k + j] -= coef * d._c[j];
        }
        return (new QPoly(q), new QPoly(rem.Take(d.Degree)));
    }

    public QPoly Monic() => IsZero ? this : (BigRational.One / Lead) * this;

    public static QPoly Gcd(QPoly a, QPoly b)
    {
        while (!b.IsZero)
        {
            var r = a.DivRem(b).Remainder;
            a = b;
            b = r;
        }
        return a.Monic();
    }

    public QPoly Derivative() => new(_c.Skip(1).Select((c, i) => c * (i + 1)));

    public BigRational Eval(BigRational x)
    {
        var r = BigRational.Zero;
        for (int i = _c.Length - 1; i >= 0; i--) r = r * x + _c[i];
        return r;
    }

    public double Eval(double x)
    {
        double r = 0;
        for (int i = _c.Length - 1; i >= 0; i--) r = r * x + _c[i].ToDouble();
        return r;
    }

    /// <summary>Splits into rational content and a primitive polynomial with integer coefficients and positive lead.</summary>
    public (BigRational Content, QPoly Primitive) ContentPrimitive()
    {
        if (IsZero) return (BigRational.Zero, this);
        BigInteger lcm = 1;
        foreach (var c in _c) lcm = lcm / BigInteger.GreatestCommonDivisor(lcm, c.Den) * c.Den;
        BigInteger g = 0;
        foreach (var c in _c) g = BigInteger.GreatestCommonDivisor(g, (c * lcm).Num);
        var content = new BigRational(g, lcm);
        if (Lead.Sign < 0) content = -content;
        return (content, new QPoly(_c.Select(c => c / content)));
    }

    public Expr ToExpr(Expr x) => Expr.Plus(_c.Select((c, i) => c.IsZero ? Expr.Zero : new Num(c) * Expr.Power(x, i)));

    /// <summary>All rational roots (without multiplicity) via the rational root theorem.</summary>
    public List<BigRational> RationalRoots()
    {
        var roots = new List<BigRational>();
        if (Degree < 1) return roots;
        var p = this;
        if (p[0].IsZero)
        {
            roots.Add(BigRational.Zero);
            int k = 0;
            while (p[k].IsZero) k++;
            p = new QPoly(p._c.Skip(k));
        }
        if (p.Degree < 1) return roots;
        var (_, prim) = p.ContentPrimitive();
        var a0 = BigInteger.Abs(prim[0].Num);
        var an = BigInteger.Abs(prim.Lead.Num);
        if (a0.GetBitLength() > 120 || an.GetBitLength() > 120) return roots;
        var ps = IntMath.Divisors(a0);
        var qs = IntMath.Divisors(an);
        var seen = new HashSet<BigRational>();
        foreach (var q in qs)
            foreach (var pp in ps)
                foreach (var cand in new[] { new BigRational(pp, q), new BigRational(-pp, q) })
                {
                    if (!seen.Add(cand)) continue;
                    if (prim.Eval(cand).IsZero) roots.Add(cand);
                }
        roots.Sort();
        return roots;
    }

    /// <summary>Square-free decomposition (Yun): returns (factor, multiplicity) pairs with monic factors.</summary>
    public List<(QPoly Factor, int Multiplicity)> SquareFree()
    {
        var result = new List<(QPoly, int)>();
        if (Degree < 1) return result;
        var f = Monic();
        var df = f.Derivative();
        var a = Gcd(f, df);
        var b = f.DivRem(a).Quotient;
        var c = df.DivRem(a).Quotient;
        var d = c - b.Derivative();
        int i = 1;
        while (b.Degree > 0)
        {
            a = Gcd(b, d);
            if (a.Degree > 0) result.Add((a.Monic(), i));
            b = b.DivRem(a).Quotient;
            c = d.DivRem(a).Quotient;
            d = c - b.Derivative();
            i++;
            if (i > 10000) break;
        }
        return result;
    }

    /// <summary>(g, s, t) with s a + t b = g = gcd(a, b) (g monic).</summary>
    public static (QPoly G, QPoly S, QPoly T) ExtendedGcd(QPoly a, QPoly b)
    {
        QPoly r0 = a, r1 = b, s0 = Constant(BigRational.One), s1 = Zero, t0 = Zero, t1 = Constant(BigRational.One);
        while (!r1.IsZero)
        {
            var (q, r) = r0.DivRem(r1);
            (r0, r1) = (r1, r);
            (s0, s1) = (s1, s0 - q * s1);
            (t0, t1) = (t1, t0 - q * t1);
        }
        if (r0.IsZero) return (r0, s0, t0);
        var inv = BigRational.One / r0.Lead;
        return (inv * r0, inv * s0, inv * t0);
    }

    /// <summary>
    /// Complete factorisation over the rationals (Zassenhaus, see <see cref="ZFactor"/>). Returns the content
    /// and primitive integer factors with multiplicities.
    /// </summary>
    public (BigRational Content, List<(QPoly Factor, int Multiplicity)> Factors) Factor()
    {
        var (content, prim) = ContentPrimitive();
        var factors = new List<(QPoly, int)>();
        if (prim.Degree < 1) return (content, factors);
        foreach (var (sqf, mult) in prim.SquareFree())
        {
            var rest = sqf.ContentPrimitive().Primitive;
            foreach (var f in ZFactor.FactorSquareFree(rest.Coeffs.Select(c => c.Num).ToArray()))
                factors.Add((new QPoly(f.Select(c => new BigRational(c))).ContentPrimitive().Primitive, mult));
        }
        // Linear factors first, then by degree, for stable output.
        factors = factors.OrderBy(t => t.Item1.Degree).ThenBy(t => -t.Item1[0].ToDouble() / t.Item1.Lead.ToDouble()).ToList();
        // Fix up the content so that content * prod(factors) == this exactly.
        var product = Constant(BigRational.One);
        foreach (var (f, m) in factors)
            for (int k = 0; k < m; k++) product *= f;
        content = Lead / product.Lead;
        return (content, factors);
    }

    /// <summary>All complex roots numerically.</summary>
    public List<System.Numerics.Complex> NumericRoots() =>
        Numerics.NumericMethods.PolynomialRoots(_c.Select(c => c.ToDouble()).ToArray());
}
