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

    /// <summary>
    /// Factors over the rationals as far as this implementation can: linear factors from rational roots and
    /// quadratic factors found by a Kronecker-style search. Returns content and primitive integer factors.
    /// </summary>
    public (BigRational Content, List<(QPoly Factor, int Multiplicity)> Factors) Factor()
    {
        var (content, prim) = ContentPrimitive();
        var factors = new List<(QPoly, int)>();
        if (prim.Degree < 1) return (content, factors);
        foreach (var (sqf, mult) in prim.SquareFree())
        {
            var rest = sqf.ContentPrimitive().Primitive;
            foreach (var r in rest.RationalRoots())
            {
                var lin = new QPoly([-r.Num, r.Den]); // r.Den*x - r.Num
                rest = rest.DivRem(lin).Quotient.ContentPrimitive().Primitive;
                factors.Add((lin, mult));
            }
            while (rest.Degree >= 4 && rest.FindQuadraticFactor() is { } quad)
            {
                factors.Add((quad, mult));
                rest = rest.DivRem(quad).Quotient.ContentPrimitive().Primitive;
            }
            if (rest.Degree >= 1) factors.Add((rest, mult));
        }
        // Fix up the content so that content * prod(factors) == this exactly.
        var product = Constant(BigRational.One);
        foreach (var (f, m) in factors)
            for (int k = 0; k < m; k++) product *= f;
        content = Lead / product.Lead;
        return (content, factors);
    }

    /// <summary>Searches an integer quadratic factor via values at -1, 0, 1 (only for primitive integer polynomials).</summary>
    private QPoly? FindQuadraticFactor()
    {
        var v0 = Eval(BigRational.Zero).Num;
        var v1 = Eval(BigRational.One).Num;
        var vm = Eval(BigRational.MinusOne).Num;
        if (v0.IsZero || v1.IsZero || vm.IsZero) return null;
        if (BigInteger.Abs(v0).GetBitLength() > 40 || BigInteger.Abs(v1).GetBitLength() > 40 || BigInteger.Abs(vm).GetBitLength() > 40) return null;
        var d0 = IntMath.Divisors(v0);
        var d1 = IntMath.Divisors(v1);
        var dm = IntMath.Divisors(vm);
        if ((long)d0.Count * d1.Count * dm.Count > 200_000) return null;
        foreach (var a0 in d0)
            foreach (var a1 in d1)
                foreach (var s1 in new[] { 1, -1 })
                    foreach (var am in dm)
                        foreach (var sm in new[] { 1, -1 })
                        {
                            // q(0)=a0 (sign fixed positive), q(1)=s1*a1, q(-1)=sm*am
                            BigInteger q0 = a0, q1 = s1 * a1, qm = sm * am;
                            var twoA = q1 + qm - 2 * q0;
                            if (!twoA.IsEven) continue;
                            var A = twoA / 2;
                            var twoB = q1 - qm;
                            if (!twoB.IsEven || A.IsZero) continue;
                            var B = twoB / 2;
                            foreach (var sign in new[] { 1, -1 })
                            {
                                var cand = new QPoly([sign * q0, sign * B, sign * A]);
                                if (cand.Lead.Sign < 0) continue;
                                var (_, r) = DivRem(cand);
                                if (r.IsZero) return cand;
                            }
                        }
        return null;
    }

    /// <summary>All complex roots numerically.</summary>
    public List<System.Numerics.Complex> NumericRoots() =>
        Numerics.NumericMethods.PolynomialRoots(_c.Select(c => c.ToDouble()).ToArray());
}
