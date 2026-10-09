using Equator.Core.Algebra;
using Equator.Core.Numerics;

namespace Equator.Core.Calculus;

public static class Series
{
    /// <summary>Taylor (or Laurent, for poles up to order 8) expansion of f about x = a up to (x-a)^n, plus an O() term.</summary>
    public static Expr Taylor(Expr f, Expr x, Expr a, int n, bool withOrderTerm = true)
    {
        if (n < 0 || n > 60) throw new MathException(Messages.T("Series order must be between 0 and 60."));
        if (a.IsInfinite) throw new MathException(Messages.T("Series about infinity is not supported; substitute x = 1/t instead."));
        var h = a.IsZero ? x : x - a;

        // Detect a pole: find the smallest m such that (x-a)^m f is finite at a.
        int m = 0;
        Expr g = f;
        var v0 = ValueAt(f, x, a);
        while ((v0 is null || v0.IsUndefinedOrInfinite) && m < 8)
        {
            m++;
            g = Rational.Cancel(Expr.Power(h, m) * f);
            v0 = ValueAt(g, x, a);
        }
        if (v0 is null || v0.IsUndefinedOrInfinite) throw new MathException(Messages.T("Could not expand: the function has an essential singularity or a branch point there."));

        var terms = new List<Expr>();
        var d = g;
        BigRational factorial = BigRational.One;
        for (int k = 0; k <= n + m; k++)
        {
            if (k > 0)
            {
                factorial *= k;
                d = Derivative.Diff(d, x);
                if (k % 3 == 0) d = Simplify(d);
            }
            var ck = k == 0 ? v0 : ValueAt(d, x, a);
            if (ck is null || ck.IsUndefinedOrInfinite) throw new MathException(Messages.T("Could not compute the series coefficients (the function may not be analytic there)."));
            ck = Solver.Tidy(ck / new Num(factorial));
            if (!ck.IsZero) terms.Add(ck * Expr.Power(h, k - m));
        }
        var sum = Expr.Plus(terms);
        return withOrderTerm ? Expr.Plus(sum, FnExpr.Inert("O", Expr.Power(h, n + 1))) : sum;
    }

    private static Expr Simplify(Expr d)
    {
        var c = Rational.Cancel(d);
        return c.Complexity() < d.Complexity() ? c : d;
    }

    private static Expr? ValueAt(Expr f, Expr x, Expr a)
    {
        var direct = f.Substitute(x, a);
        if (!direct.IsUndefinedOrInfinite) return direct;
        var lim = Limits.Limit(f, x, a);
        return lim is FnExpr { Name: "limit" } ? null : lim;
    }
}

public static class Sums
{
    private const int DirectLimit = 20000;

    /// <summary>Σ f for k from a to b; the index is an integer.</summary>
    public static Expr Sum(Expr f, Expr k, Expr a, Expr b) =>
        Engine.Assumptions.WithVariable(k, "integer", () => SumCore(Engine.Assumptions.Refine(f), k, a, b));

    private static Expr SumCore(Expr f, Expr k, Expr a, Expr b)
    {
        if (k is not Sym) throw new MathException(Messages.T("Summation index must be a symbol."));
        if (a is Num na && b is Num nb && na.Value.IsInteger && nb.Value.IsInteger)
        {
            var lo = na.Value.Num; var hi = nb.Value.Num;
            if (hi < lo) return Expr.Zero;
            if (hi - lo <= DirectLimit && (f.IsConstant || hi - lo <= 2000 || f.FreeSymbols().All(s => s == k)))
            {
                var terms = new List<Expr>();
                for (var i = lo; i <= hi; i++) terms.Add(f.Substitute(k, new Num(i)));
                return Expr.Plus(terms);
            }
        }

        if (f.FreeOf(k)) return f * (b - a + 1);

        if (b == Expr.Infinity) return Infinite(f, k, a);

        // Polynomial summand: closed form by interpolation of the partial sums.
        var poly = PolyView.Coefficients(f, k);
        if (poly is not null && poly.Count <= 30)
        {
            var nSym = new Sym("\u0001n");
            var S = PolynomialPartialSum(f, k, poly.Count - 1, nSym);
            return Factoring.Factor(Expansion.Expand(S.Substitute(nSym, b) - S.Substitute(nSym, a - 1)));
        }

        // Geometric: ratio f(k+1)/f(k) independent of k.
        var ratio = Ratio(f, k);
        if (ratio is not null && ratio.FreeOf(k) && !ratio.IsOne)
            return Solver.Tidy(f.Substitute(k, a) * (Expr.Power(ratio, b - a + 1) - 1) / (ratio - 1));

        return FnExpr.Inert("sum", f, k, a, b);
    }

    private static Expr? Ratio(Expr f, Expr k)
    {
        try
        {
            // (c k + d + c)! = (c k + d)! * prod_{i=1..c} (c k + d + i), so factorials cancel in the ratio.
            var shifted = f.Substitute(k, k + 1).MapBottomUp(z =>
            {
                if (z is FnExpr { Name: "factorial" } fz && PolyView.Coefficients(fz.Args[0], k) is { Count: 2 } lin &&
                    lin[1] is Num { Value.IsInteger: true } step && step.Value.Sign > 0 && step.Value.Num <= 8)
                {
                    var original = fz.Args[0].Substitute(k, k - 1);
                    var prod = Expr.Times(Enumerable.Range(1, (int)step.Value.Num).Select(i => original + i));
                    return FnExpr.Inert("factorial", original) * prod;
                }
                return z;
            });
            return Algebra.Simplifier.Simplify(Rational.Cancel(Expansion.Expand(shifted / f)));
        }
        catch (MathException) { return null; }
    }

    /// <summary>S(n) = sum_{k=0}^{n} f(k), a polynomial of degree d+1, found by Lagrange interpolation.</summary>
    private static Expr PolynomialPartialSum(Expr f, Expr k, int degree, Sym n)
    {
        int pts = degree + 2;
        var xs = new List<int>();
        var ys = new List<Expr>();
        Expr acc = Expr.Zero;
        for (int i = 0; i < pts; i++)
        {
            acc = Expansion.Expand(acc + f.Substitute(k, i));
            xs.Add(i);
            ys.Add(acc);
        }
        var terms = new List<Expr>();
        for (int i = 0; i < pts; i++)
        {
            Expr basis = Expr.One;
            for (int j = 0; j < pts; j++)
                if (j != i) basis = basis * (n - xs[j]) / (xs[i] - xs[j]);
            terms.Add(ys[i] * basis);
        }
        return Expansion.Expand(Expr.Plus(terms));
    }

    private static Expr Infinite(Expr f, Expr k, Expr a)
    {
        // Terms must tend to zero.
        var lim = Limits.Limit(f, k, Expr.Infinity);
        if (lim.IsConstant && !lim.IsZero && !(lim is FnExpr))
            return lim.IsNegativeNumber || (lim.IsInfinite && lim.HasNegativeCoefficient) ? -Expr.Infinity : Expr.Infinity;

        var ratio = Ratio(f, k);
        if (ratio is not null && ratio.FreeOf(k))
        {
            if (ratio.IsConstant && !ratio.Contains(Expr.I))
            {
                var r = NumericEval.EvalReal(ratio);
                if (Math.Abs(r) >= 1) return r > 0 ? Expr.Infinity : Expr.Undefined;
            }
            return Solver.Tidy(f.Substitute(k, a) / (1 - ratio));
        }

        // Exponential series: ratio y/(k + c)
        if (ratio is not null && Expansion.Expand(Expr.One / ratio) is var inv && PolyView.Coefficients(inv, k) is { Count: 2 } lin)
        {
            var y = Expr.One / lin[1];
            var c = lin[0] * y;
            if (c is Num { Value.IsInteger: true } cn)
            {
                // f(k) = C y^k / (k + c - 1)!  => sum from k=a = C' (e^y - partial)
                // Normalise: f(k) = A * y^(k+c-1)/(k+c-1)!, A = f(a) * (a+c-1)! / y^(a+c-1)
                var shift = a + cn - 1;
                if (shift is Num { Value.IsInteger: true } sh && sh.Value.Sign >= 0 && sh.Value.Num < 50)
                {
                    var A = Solver.Tidy(f.Substitute(k, a) * Expr.Call("factorial", shift) / Expr.Power(y, shift));
                    var partial = Expr.Plus(Enumerable.Range(0, (int)sh.Value.Num).Select(j => Expr.Power(y, j) / Expr.Call("factorial", j)));
                    return Solver.Tidy(A * (Expr.Exp(y) - partial));
                }
            }
        }

        // p-series Σ 1/k^p = ζ(p) − Σ_{j<a} 1/j^p (exact for even p, zeta(p) otherwise).
        if (f is PowExpr { Base: var bk, Exponent: var pe } && bk == k && pe.FreeOf(k)
            && a is Num { Value.IsInteger: true } first && first.Value.Num >= 1 && first.Value.Num < 1000)
        {
            var p = -pe;
            if (p.IsOne) return Expr.Infinity;
            double pv = p.IsConstant ? NumericEval.EvalReal(p) : double.NaN;
            if (p.IsConstant && !(pv > 1)) return pv <= 1 ? Expr.Infinity : FnExpr.Inert("sum", f, k, a, Expr.Infinity);
            var zeta = p is Num { Value.IsInteger: true } pi && pi.Value.Num <= 30 && pi.Value.Num.IsEven ? Zeta((int)pi.Value.Num) : Expr.Call("zeta", p);
            var head = Expr.Plus(Enumerable.Range(1, (int)first.Value.Num - 1).Select(j => Expr.Power(new Num(j), -p)));
            return zeta - head;
        }

        // Alternating p-series: Σ_{k≥1} (−1)^k / k^p = −(1 − 2^(1−p)) ζ(p)  (= −log 2 for p = 1).
        // c (−1)^(k+s) / k^p with constants c and integer s.
        if (f is MulExpr am && a is Num { Value.IsInteger: true } afirst && afirst.Value.Num >= 1 && afirst.Value.Num < 1000)
        {
            var sign = am.Factors.FirstOrDefault(z => z is PowExpr { Base: Num { Value.IsMinusOne: true } } ps && Expansion.Expand(ps.Exponent - k) is Num { Value.IsInteger: true });
            var power = am.Factors.FirstOrDefault(z => z is PowExpr { Base: var pb2, Exponent: var pe2 } && pb2 == k && pe2.FreeOf(k));
            var others = am.Factors.Where(z => !ReferenceEquals(z, sign) && !ReferenceEquals(z, power)).ToList();
            if (sign is PowExpr ps2 && power is PowExpr pk && others.All(z => z.FreeOf(k)))
            {
                var shift = (Num)Expansion.Expand(ps2.Exponent - k);
                var c = Expr.Times(others) * Expr.Power(-1, shift);
                var p = -pk.Exponent;
                double pv = p.IsConstant ? NumericEval.EvalReal(p) : double.NaN;
                if (pv > 0 || !p.IsConstant)
                {
                    var total = p.IsOne ? -Expr.Call("log", 2) : -(1 - Expr.Power(2, 1 - p)) * Expr.Call("zeta", p);
                    var head = Expr.Plus(Enumerable.Range(1, (int)afirst.Value.Num - 1).Select(j => Expr.Power(-1, j) * Expr.Power(new Num(j), -p)));
                    return Solver.Tidy(c * (total - head));
                }
            }
        }

        // Telescoping rational sums via partial fractions.
        var tele = Telescoping(f, k, a);
        if (tele is not null) return tele;

        // Numeric fallback.
        if (f.FreeSymbols().All(s => s == k) && a.IsInteger)
        {
            var fn = NumericEval.Compile1(f, ((Sym)k).Name);
            long start = (long)((Num)a).Value.Num;
            var alt = AlternatingSum(fn, start);
            if (alt is not null) return Limits.Recognize(alt.Value);
            double sum = 0, c2 = 0;
            double lastTerm = double.NaN;
            for (long i = start; i < start + 2_000_000; i++)
            {
                double t = fn(i);
                if (!double.IsFinite(t)) throw new MathException(Messages.T("The series has a non-finite term."));
                // Kahan summation
                var y = t - c2; var s2 = sum + y; c2 = (s2 - sum) - y; sum = s2;
                lastTerm = t;
                if (i - start > 100 && Math.Abs(t) < 1e-17 * Math.Max(1, Math.Abs(sum))) return new Real(sum);
            }
            throw new MathException(Messages.T("The series converges too slowly to sum numerically (or diverges)."));
        }
        return FnExpr.Inert("sum", f, k, a, Expr.Infinity);
    }

    /// <summary>Cohen-Villegas-Zagier acceleration for alternating series; null if the terms do not alternate.</summary>
    private static double? AlternatingSum(Func<double, double> term, long start)
    {
        const int n = 40;
        var t = Enumerable.Range(0, n).Select(i => term(start + i)).ToArray();
        if (t.Any(v => !double.IsFinite(v)) || t[0] == 0) return null;
        for (int i = 1; i < n; i++) if (Math.Sign(t[i]) == Math.Sign(t[i - 1]) || t[i] == 0) return null;
        var ak = t.Select((v, i) => i % 2 == 0 ? v : -v).ToArray(); // t_i = (-1)^i a_i
        double d = Math.Pow(3 + Math.Sqrt(8), n);
        d = (d + 1 / d) / 2;
        double b = -1, c = -d, s = 0;
        for (int i = 0; i < n; i++)
        {
            c = b - c;
            s += c * ak[i];
            b = (i + n) * (i - n) * b / ((i + 0.5) * (i + 1));
        }
        return s / d;
    }

    private static Expr? Telescoping(Expr f, Expr k, Expr a)
    {
        if (a is not Num { Value.IsInteger: true } an) return null;
        var apart = Rational.Apart(f, k);
        var terms = apart is AddExpr s ? s.Terms : [apart];
        var parts = new List<(Expr A, BigRational c)>();
        foreach (var t in terms)
        {
            // A / (k + c)
            var (n, d) = Rational.NumDen(t);
            if (!n.FreeOf(k)) return null;
            var lin = PolyView.Coefficients(d, k);
            if (lin is not { Count: 2 } || lin[1] is not Num slope || lin[0] is not Num off) return null;
            var c = off.Value / slope.Value;
            if (!c.IsInteger) return null;
            parts.Add((n / slope, c));
        }
        if (!Expr.Plus(parts.Select(p => p.A)).IsZero) return null;
        // sum_{k>=a} 1/(k+c) = H_inf - H_{a+c-1}; the H_inf parts cancel.
        var result = new List<Expr>();
        foreach (var (A, c) in parts)
        {
            var upto = (int)(an.Value + c - 1).Num;
            if (upto < 0) return null;
            var h = Expr.Plus(Enumerable.Range(1, upto).Select(j => (Expr)new Num(new BigRational(1, j))));
            result.Add(-A * h);
        }
        return Expr.Plus(result);
    }

    /// <summary>ζ(2n) = (-1)^(n+1) B_2n (2π)^(2n) / (2 (2n)!)</summary>
    public static Expr Zeta(int p)
    {
        var b = Bernoulli(p);
        var coef = b * BigRational.Pow(2, p) / (2 * new BigRational(IntMath.Factorial(p)));
        if ((p / 2) % 2 == 0) coef = -coef;
        return new Num(coef) * Expr.Power(Expr.Pi, p);
    }

    private static readonly List<BigRational> BernoulliTable = [BigRational.One];

    /// <summary>Bernoulli number B_n (B_1 = −1/2), from a cached table grown on demand.</summary>
    public static BigRational Bernoulli(int n)
    {
        lock (BernoulliTable)
        {
            for (int m = BernoulliTable.Count; m <= n; m++)
            {
                var s = BigRational.Zero;
                for (int j = 0; j < m; j++) s += new BigRational(IntMath.Binomial(m + 1, j)) * BernoulliTable[j];
                BernoulliTable.Add(-s / (m + 1));
            }
            return BernoulliTable[n];
        }
    }

    public static Expr Product(Expr f, Expr k, Expr a, Expr b)
    {
        if (a is Num na && b is Num nb && na.Value.IsInteger && nb.Value.IsInteger && nb.Value.Num - na.Value.Num <= DirectLimit)
        {
            var factors = new List<Expr>();
            for (var i = na.Value.Num; i <= nb.Value.Num; i++) factors.Add(f.Substitute(k, new Num(i)));
            return Expr.Times(factors);
        }
        if (f == k && a.IsOne) return Expr.Call("factorial", b);
        if (f.FreeOf(k)) return Expr.Power(f, b - a + 1);
        return FnExpr.Inert("product", f, k, a, b);
    }
}
