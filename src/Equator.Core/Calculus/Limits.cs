using Equator.Core.Algebra;
using Equator.Core.Numerics;

namespace Equator.Core.Calculus;

/// <summary>
/// Limits by structural recursion (continuity), L'Hôpital's rule for indeterminate quotients, rewriting of
/// 0·∞, ∞−∞ and exponential forms, with a numeric fallback that recognises simple closed forms.
/// </summary>
public static class Limits
{
    /// <param name="dir">0 = two-sided, +1 = from the right (x → a⁺), −1 = from the left (x → a⁻).</param>
    public static Expr Limit(Expr f, Expr x, Expr a, int dir = 0)
    {
        if (x is not Sym) throw new MathException("Limit variable must be a symbol.");
        if (f.FreeOf(x)) return f;
        if (f is ListExpr l) return Expr.List(l.Items.Select(i => Limit(i, x, a, dir)));

        if (a == Expr.Infinity || (a.IsInfinite && a.HasNegativeCoefficient))
        {
            // x -> ±∞ becomes t -> 0⁺ with x = ±1/t
            var t = new Sym("\u0001t");
            var g = f.Substitute(x, a == Expr.Infinity ? Expr.One / t : -Expr.One / t);
            var r0 = Limit(g, t, Expr.Zero, +1);
            // Never leak the internal variable into an unevaluated result.
            return r0.Contains(t) ? FnExpr.Inert("limit", f, x, a) : r0;
        }

        if (dir == 0)
        {
            var right = Limit(f, x, a, +1);
            var left = Limit(f, x, a, -1);
            if (right == left) return right;
            if (IsCloseNumerically(right, left)) return right;
            return Expr.Undefined;
        }

        var r = Lim(f, x, a, dir, 0);
        if (r is not null && r != Expr.Undefined && r != Expr.ComplexInfinity) return Clean(r);
        var n = NumericLimit(f, x, a, dir);
        if (n is not null) return n;
        return r ?? FnExpr.Inert("limit", f, x, a);
    }

    private static bool IsCloseNumerically(Expr a, Expr b)
    {
        if (!a.IsConstant || !b.IsConstant || a.IsUndefinedOrInfinite || b.IsUndefinedOrInfinite) return false;
        try
        {
            var va = NumericEval.Eval(a); var vb = NumericEval.Eval(b);
            return System.Numerics.Complex.Abs(va - vb) < 1e-9 * (1 + System.Numerics.Complex.Abs(va));
        }
        catch (MathException) { return false; }
    }

    private static Expr Clean(Expr r)
    {
        if (r.IsInfinite) return r;
        return Solver.Tidy(r);
    }

    private static bool Indeterminate(Expr e) => e == Expr.Undefined || e == Expr.ComplexInfinity;
    private static bool IsInf(Expr e) => e.IsInfinite;

    /// <summary>Structural limit; null when it cannot be decided this way.</summary>
    private static Expr? Lim(Expr f, Expr x, Expr a, int dir, int depth)
    {
        if (depth > 25) return null;
        Engine.Interrupt.Check();
        if (f.FreeOf(x)) return f;
        if (f == x) return a;

        switch (f)
        {
            case AddExpr s:
                {
                    var parts = new List<Expr>();
                    foreach (var t in s.Terms)
                    {
                        var lt = Lim(t, x, a, dir, depth + 1);
                        if (lt is null || Indeterminate(lt)) return Quotient(f, x, a, dir, depth);
                        parts.Add(lt);
                    }
                    var sum = Expr.Plus(parts);
                    if (!Indeterminate(sum)) return sum;
                    // ∞ − ∞: combine over a common denominator and try again.
                    return Quotient(f, x, a, dir, depth);
                }
            case MulExpr m:
                {
                    // 0 · bounded = 0 (x sin(1/x) as x → 0, sin(x)/x as x → ∞).
                    if (ZeroTimesBounded(m, x, a, dir, depth)) return Expr.Zero;
                    var parts = new List<Expr>();
                    bool bad = false;
                    foreach (var t in m.Factors)
                    {
                        var lt = Lim(t, x, a, dir, depth + 1);
                        if (lt is null || Indeterminate(lt)) { bad = true; break; }
                        parts.Add(lt);
                    }
                    if (!bad)
                    {
                        var prod = Expr.Times(parts);
                        if (!Indeterminate(prod)) return prod;
                    }
                    return Quotient(f, x, a, dir, depth) ?? ViaLogarithm(m, x, a, dir, depth);
                }
            case PowExpr p:
                {
                    var lb = Lim(p.Base, x, a, dir, depth + 1);
                    var le = p.Exponent.FreeOf(x) ? p.Exponent : Lim(p.Exponent, x, a, dir, depth + 1);
                    if (lb is not null && le is not null && !Indeterminate(lb) && !Indeterminate(le))
                    {
                        if (lb.IsZero && le.IsNumber && le.IsNegativeNumber)
                        {
                            // 1/0: sign from the side we approach on.
                            var sign = SideSign(p.Base, x, a, dir);
                            if (sign == 0) return Expr.ComplexInfinity;
                            bool evenInt = le is Num { Value.IsInteger: true } k && k.Value.Num.IsEven;
                            return evenInt || sign > 0 ? Expr.Infinity : -Expr.Infinity;
                        }
                        if (lb == Expr.Infinity && IsInf(le)) return le == Expr.Infinity ? Expr.Infinity : Expr.Zero;
                        bool indeterminateForm =
                            (lb.IsOne && IsInf(le)) || (lb.IsZero && le.IsZero) || (IsInf(lb) && le.IsZero);
                        if (!indeterminateForm)
                        {
                            var v = Expr.Power(lb, le);
                            if (!Indeterminate(v)) return v;
                        }
                    }
                    if (p.Exponent.FreeOf(x)) return Quotient(f, x, a, dir, depth);
                    // f = e^(exponent * log(base))
                    var expo = Lim(p.Exponent * Expr.Call("log", p.Base), x, a, dir, depth + 1);
                    if (expo is null || Indeterminate(expo)) return null;
                    return Expr.Exp(expo);
                }
            case FnExpr fn when fn.Args.Count == 1:
                {
                    var lu = Lim(fn.Args[0], x, a, dir, depth + 1);
                    if (lu is null || Indeterminate(lu)) return null;
                    if (IsInf(lu))
                    {
                        bool pos = lu == Expr.Infinity;
                        switch (fn.Name)
                        {
                            case "log": return pos ? Expr.Infinity : null;
                            case "atan": return pos ? Expr.Pi / 2 : -Expr.Pi / 2;
                            case "tanh": case "erf": return pos ? Expr.One : Expr.MinusOne;
                            case "sinh": case "asinh": return pos ? Expr.Infinity : -Expr.Infinity;
                            case "cosh": return Expr.Infinity;
                            case "acot": return Expr.Zero;
                            case "sqrt": return pos ? Expr.Infinity : null;
                            case "abs": return Expr.Infinity;
                            case "factorial": case "gamma": return pos ? Expr.Infinity : null;
                            case "sin": case "cos": return Expr.Undefined;
                            default: return null;
                        }
                    }
                    if (fn.Name == "log" && lu.IsZero)
                        return Expr.MinusOne * Expr.Infinity;
                    if (fn.Name is "tan" or "sec" or "csc" or "cot" or "sign")
                    {
                        // Continuous wherever the value is finite (and, for sign, away from 0).
                        var w = Expr.Call(fn.Name, lu);
                        if (!w.IsUndefinedOrInfinite && !(fn.Name == "sign" && lu.IsZero) && (!w.IsConstant || IsFiniteNumber(w))) return w;
                        if (w == Expr.ComplexInfinity && fn.Name != "sign")
                        {
                            // A pole: the side of approach decides the sign.
                            var s = SideSign(f, x, a, dir);
                            return s > 0 ? Expr.Infinity : s < 0 ? -Expr.Infinity : null;
                        }
                        return null;
                    }
                    if (fn.Name == "abs") return Expr.Call("abs", lu);
                    if (fn.Name is "floor" or "ceil" or "round")
                    {
                        // Possibly discontinuous: evaluate near the point instead.
                        return null;
                    }
                    var v = Expr.Call(fn.Name, lu);
                    return Indeterminate(v) ? null : v;
                }
        }
        return null;
    }

    private static bool IsFiniteNumber(Expr e)
    {
        try { var v = NumericEval.Eval(e); return double.IsFinite(v.Real) && double.IsFinite(v.Imaginary); }
        catch (MathException) { return false; }
    }

    /// <summary>True when one factor tends to 0 and every other factor stays bounded near the point.</summary>
    private static bool ZeroTimesBounded(MulExpr m, Expr x, Expr a, int dir, int depth)
    {
        int zeroIndex = -1;
        for (int i = 0; i < m.Factors.Count && zeroIndex < 0; i++)
        {
            if (m.Factors[i].FreeOf(x) || IsBounded(m.Factors[i])) continue;
            var l = Lim(m.Factors[i], x, a, dir, depth + 1);
            if (l is not null && l.IsZero) zeroIndex = i;
        }
        if (zeroIndex < 0) return false;
        for (int i = 0; i < m.Factors.Count; i++)
        {
            if (i == zeroIndex || m.Factors[i].FreeOf(x) || IsBounded(m.Factors[i])) continue;
            var l = Lim(m.Factors[i], x, a, dir, depth + 1);
            if (l is null || Indeterminate(l) || l.IsInfinite) return false;
        }
        return true;
    }

    /// <summary>Bounded for real arguments: sin, cos, atan, tanh, erf and their sums, products and positive powers.</summary>
    private static bool IsBounded(Expr e) => e switch
    {
        Num or Real => true,
        Sym s => s.IsNumericConstant && s != Expr.I,
        FnExpr { Name: "sin" or "cos" or "atan" or "tanh" or "erf" or "sign" or "acot" } f => !f.Args[0].Contains(Expr.I),
        AddExpr s => s.Terms.All(IsBounded),
        MulExpr p => p.Factors.All(IsBounded),
        PowExpr { Exponent: Num { Value.IsInteger: true, Value.Sign: > 0 } } p => IsBounded(p.Base),
        _ => false,
    };

    /// <summary>
    /// For a product of factors that are positive near the point, lim f = exp(lim Σ log fᵢ). Taking logarithms
    /// turns growth comparisons (e^x / x^100, n!/n^n) into sums that the ∞ − ∞ machinery handles; log(u!)
    /// is replaced by Stirling's form u log u − u + log(2πu)/2 when u → ∞.
    /// </summary>
    private static Expr? ViaLogarithm(MulExpr m, Expr x, Expr a, int dir, int depth)
    {
        if (depth > 12 || !a.IsConstant || !m.FreeSymbols().All(s => s == x)) return null;
        var logs = new List<Expr>();
        Expr constant = Expr.One;
        foreach (var fac in m.Factors)
        {
            if (fac.FreeOf(x)) { constant *= fac; continue; }
            var lg = LogOf(fac, x, a, dir, depth);
            if (lg is null) return null;
            logs.Add(lg);
        }
        var L = Lim(Expansion.Expand(Expr.Plus(logs)), x, a, dir, depth + 1);
        if (L is null || Indeterminate(L)) return null;
        if (L == Expr.Infinity) return constant * Expr.Infinity;
        if (L.IsInfinite) return Expr.Zero;
        return constant * Expr.Exp(L);
    }

    private static Expr? LogOf(Expr fac, Expr x, Expr a, int dir, int depth)
    {
        switch (fac)
        {
            case PowExpr { Base: Sym { Name: "E" } } pe:
                return pe.Exponent;
            case PowExpr p when p.Base.FreeOf(x) ? p.Base.IsConstant && IsFiniteNumber(p.Base) && NumericSign(p.Base) > 0 : PositiveNear(p.Base, x, a, dir, depth):
                {
                    var inner = p.Base.FreeOf(x) ? Expr.Call("log", p.Base) : LogOf(p.Base, x, a, dir, depth);
                    return inner is null ? null : p.Exponent * inner;
                }
            case MulExpr mm when mm.Factors.All(z => z.FreeOf(x) ? z.IsConstant && IsFiniteNumber(z) && NumericSign(z) > 0 : PositiveNear(z, x, a, dir, depth)):
                {
                    var parts = new List<Expr>();
                    foreach (var z in mm.Factors)
                    {
                        var lz = z.FreeOf(x) ? Expr.Call("log", z) : LogOf(z, x, a, dir, depth);
                        if (lz is null) return null;
                        parts.Add(lz);
                    }
                    return Expr.Plus(parts);
                }
            case FnExpr { Name: "factorial" or "gamma" } g:
                {
                    var u = g.Name == "gamma" ? g.Args[0] - 1 : g.Args[0];
                    if (Lim(u, x, a, dir, depth + 1) != Expr.Infinity) return null;
                    // u > 0 here, so its logarithm may be expanded (log(1/t) = −log(t)) for cancellation.
                    var lu = LogOf(u, x, a, dir, depth) ?? Expr.Call("log", u);
                    return u * lu - u + (Expr.Call("log", 2 * Expr.Pi) + lu) / 2;
                }
        }
        return PositiveNear(fac, x, a, dir, depth) ? Expr.Call("log", fac) : null;
    }

    /// <summary>Positivity beside the point, structurally where possible (e^u, u! with u → ∞), else numerically.</summary>
    private static bool PositiveNear(Expr e, Expr x, Expr a, int dir, int depth) => e switch
    {
        PowExpr { Base: Sym { Name: "E" } } => true,
        FnExpr { Name: "factorial" or "gamma" } g => Lim(g.Args[0], x, a, dir, depth + 1) == Expr.Infinity || SideSign(e, x, a, dir) > 0,
        PowExpr p when PositiveNear(p.Base, x, a, dir, depth) => true,
        _ => SideSign(e, x, a, dir) > 0,
    };

    /// <summary>Writes f as N/D and applies L'Hôpital's rule to 0/0 and ∞/∞ forms.</summary>
    private static Expr? Quotient(Expr f, Expr x, Expr a, int dir, int depth)
    {
        if (depth > 25) return null;
        var (n, d) = Rational.NumDen(Rational.Together(f));
        if (d.FreeOf(x))
        {
            // 0·∞ inside a product: move the infinite factor to the denominator as its reciprocal.
            if (f is MulExpr m && m.Factors.Count >= 2)
            {
                foreach (var fac in m.Factors)
                {
                    if (fac.FreeOf(x)) continue;
                    var lf = Lim(fac, x, a, dir, depth + 1);
                    if (lf is not null && lf.IsZero)
                    {
                        var others = Expr.Times(m.Factors.Where(z => !ReferenceEquals(z, fac)));
                        return LHopital(others, Expr.One / fac, x, a, dir, depth);
                    }
                }
            }
            return null;
        }
        return LHopital(n, d, x, a, dir, depth);
    }

    private static Expr? LHopital(Expr n, Expr d, Expr x, Expr a, int dir, int depth)
    {
        for (int i = 0; i < 8; i++)
        {
            var ln = Lim(n, x, a, dir, depth + 1);
            var ld = Lim(d, x, a, dir, depth + 1);
            if (ln is null || ld is null) return null;
            if (Indeterminate(ln) || Indeterminate(ld)) return null;
            bool zeroZero = ln.IsZero && ld.IsZero;
            bool infInf = IsInf(ln) && IsInf(ld);
            if (!zeroZero && !infInf)
            {
                if (ld.IsZero)
                {
                    if (ln.IsZero) return null;
                    var s = SideSign(d, x, a, dir) * NumericSign(ln);
                    if (s == 0) return Expr.ComplexInfinity;
                    return s > 0 ? Expr.Infinity : -Expr.Infinity;
                }
                var q = ln / ld;
                return Indeterminate(q) ? null : q;
            }
            n = Rational.Cancel(Derivative.Diff(n, x));
            d = Rational.Cancel(Derivative.Diff(d, x));
            // Cancel the new quotient before the next round to avoid growth.
            var (n2, d2) = Rational.NumDen(Rational.Cancel(n / d));
            n = n2; d = d2;
            if (d.FreeOf(x)) return Lim(n / d, x, a, dir, depth + 1);
        }
        return null;
    }

    private static int NumericSign(Expr e)
    {
        if (e == Expr.Infinity) return 1;
        if (e.IsInfinite) return e.HasNegativeCoefficient ? -1 : 0;
        try { return Math.Sign(NumericEval.EvalReal(e)); }
        catch (MathException) { return 0; }
    }

    /// <summary>Sign of g(x) just beside a on the given side (0 when unknown).</summary>
    private static int SideSign(Expr g, Expr x, Expr a, int dir)
    {
        if (!g.FreeSymbols().All(s => s == x) || !a.IsConstant) return 0;
        try
        {
            var fn = NumericEval.Compile1(g, ((Sym)x).Name);
            double av = NumericEval.EvalReal(a);
            int sign = 0;
            foreach (var h in new[] { 1e-6, 1e-8, 1e-10 })
            {
                var v = fn(av + dir * h * Math.Max(1, Math.Abs(av)));
                if (double.IsNaN(v)) return 0;
                int s = Math.Sign(v);
                if (sign != 0 && s != sign) return 0;
                sign = s;
            }
            return sign;
        }
        catch (MathException) { return 0; }
    }

    // ---- numeric fallback ------------------------------------------------------------------------

    private static Expr? NumericLimit(Expr f, Expr x, Expr a, int dir)
    {
        if (!f.FreeSymbols().All(s => s == x) || !a.IsConstant) return null;
        Func<double, double> fn;
        try { fn = NumericEval.Compile1(f, ((Sym)x).Name); }
        catch (MathException) { return null; }
        double av;
        try { av = NumericEval.EvalReal(a); } catch (MathException) { return null; }
        if (double.IsNaN(av)) return null;

        double scale = Math.Max(1, Math.Abs(av));
        double F(double h) => fn(av + dir * h * scale);

        var values = new List<double>();
        for (int k = 3; k <= 9; k++) values.Add(F(Math.Pow(10, -k)));
        // NaN means outside the domain; ±∞ means floating-point overflow, which says nothing about the limit.
        if (values.Any(v => !double.IsFinite(v))) return null;
        if (values.Skip(3).All(v => Math.Abs(v) > 1e6) && values.Zip(values.Skip(1)).All(p => Math.Abs(p.Second) >= Math.Abs(p.First)))
            return Math.Sign(values[^1]) > 0 ? Expr.Infinity : -Expr.Infinity;

        // Richardson extrapolation assuming f(a + h) = L + c1 h + c2 h^2 + ...
        double Extrapolate(double h)
        {
            double f1 = F(h), f2 = F(h / 2), f3 = F(h / 4);
            double r1 = 2 * f2 - f1, r2 = 2 * f3 - f2;
            return (4 * r2 - r1) / 3;
        }
        double l1 = Extrapolate(1e-3), l2 = Extrapolate(1e-4);
        if (!double.IsFinite(l1) || !double.IsFinite(l2)) return null;
        if (Math.Abs(l1 - l2) > 1e-6 * (1 + Math.Abs(l2)))
        {
            // Slowly converging (e.g. fractional powers): accept only a plainly settled sequence.
            var last = values[^1];
            if (Math.Abs(values[^1] - values[^2]) < 1e-7 * (1 + Math.Abs(last))) return Recognize(last, 1e-6);
            return null;
        }
        // A floating-point answer is only offered when the extrapolation has clearly converged; otherwise the
        // "limit" would just be rounding noise.
        var exact = Recognize(l2);
        if (exact is not Real) return exact;
        return Math.Abs(l1 - l2) < 1e-9 * (1 + Math.Abs(l2)) ? exact : null;
    }

    /// <summary>Tries to identify a floating value as a simple exact constant.</summary>
    public static Expr Recognize(double v, double tolerance = 1e-9)
    {
        if (Math.Abs(v) < tolerance) return Expr.Zero;
        Expr? Try(double target, Func<BigRational, Expr> build)
        {
            if (!double.IsFinite(target)) return null;
            var r = BigRational.FromDouble(target, 200);
            if (Math.Abs(r.ToDouble() - target) < tolerance * (1 + Math.Abs(target))) return build(r);
            return null;
        }
        return Try(v, r => new Num(r))
            ?? Try(v / Math.PI, r => new Num(r) * Expr.Pi)
            ?? Try(v * v, r => r.Sign > 0 ? (v < 0 ? -Expr.Sqrt(new Num(r)) : Expr.Sqrt(new Num(r))) : new Real(v))
            ?? Try(Math.Log(Math.Abs(v)), r => (v < 0 ? -Expr.One : Expr.One) * Expr.Exp(new Num(r)))
            ?? Try(v / Math.E, r => new Num(r) * Expr.E)
            ?? (Expr)new Real(v);
    }
}
