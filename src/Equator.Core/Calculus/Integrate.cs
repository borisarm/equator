using Equator.Core.Algebra;
using Equator.Core.Numerics;

namespace Equator.Core.Calculus;

/// <summary>
/// Heuristic symbolic integration (in the spirit of a "derivative-divides" integrator): a table with linear
/// arguments, rational functions by partial fractions, substitution, trigonometric rewriting, integration by
/// parts and quadratic radicals. Returns null when no antiderivative is found.
/// </summary>
public static class Integrator
{
    private const int MaxDepth = 7;

    /// <summary>Antiderivative on the real line (the integration variable is taken to be real).</summary>
    public static Expr? Antiderivative(Expr f, Expr x) =>
        Engine.Assumptions.WithVariable(x, "real", () => AntiderivativeCore(Engine.Assumptions.Refine(f), x));

    private static Expr? AntiderivativeCore(Expr f, Expr x)
    {
        if (x is not Sym) throw new MathException("Integration variable must be a symbol.");
        var r = Integrate(f, x, 0);
        if (r is null) return null;
        r = Tidy(r);
        // The heuristics above can be fooled by substitutions that only hold on part of the real line
        // (e.g. x = sqrt(t)); never return a result that does not differentiate back to the integrand.
        return Verification.IsAntiderivative(r, f, x) ? r : null;
    }

    /// <summary>Antiderivatives read best expanded (2x sin(x) + 2cos(x) rather than 2(x sin(x) + cos(x))).</summary>
    private static Expr Tidy(Expr e)
    {
        var expanded = Expansion.ExpandShallow(e);
        return expanded.Complexity() <= e.Complexity() * 1.3 + 2 ? expanded : e;
    }

    private static Expr? Integrate(Expr f, Expr x, int depth)
    {
        if (depth > MaxDepth) return null;
        Engine.Interrupt.Check();
        if (f.FreeOf(x)) return f * x;
        if (f == x) return Expr.Power(x, 2) / 2;

        if (f is AddExpr sum)
        {
            var parts = new List<Expr>();
            foreach (var t in sum.Terms)
            {
                var r = Integrate(t, x, depth);
                if (r is null)
                {
                    // Splitting may separate terms that only integrate together; try the rational route on the whole.
                    return RationalIntegrate(f, x) ?? ExpandRetry(f, x, depth);
                }
                parts.Add(r);
            }
            return Expr.Plus(parts);
        }

        // Pull out constant factors.
        if (f is MulExpr m)
        {
            var constant = m.Factors.Where(t => t.FreeOf(x)).ToList();
            if (constant.Count > 0)
            {
                var rest = Expr.Times(m.Factors.Where(t => !t.FreeOf(x)));
                var r = Integrate(rest, x, depth);
                return r is null ? null : Expr.Times(constant) * r;
            }
        }

        return Table(f, x)
            ?? RationalIntegrate(f, x)
            ?? Substitution(f, x, depth)
            ?? Trig(f, x, depth)
            ?? ByParts(f, x, depth)
            ?? QuadraticRadical(f, x)
            ?? ExpandRetry(f, x, depth)
            ?? Weierstrass(f, x, depth);
    }

    private static Expr? ExpandRetry(Expr f, Expr x, int depth)
    {
        var ex = Expansion.Expand(f);
        if (ex == f) return null;
        return Integrate(ex, x, depth + 1);
    }

    /// <summary>If u = a x + b with a != 0 and a, b free of x, returns a.</summary>
    private static Expr? LinearSlope(Expr u, Expr x)
    {
        if (u == x) return Expr.One;
        var c = PolyView.Coefficients(u, x);
        if (c is null || c.Count != 2 || c[1].IsZero) return null;
        return c[1];
    }

    // ---- table --------------------------------------------------------------------------------------

    private static Expr? Table(Expr f, Expr x)
    {
        switch (f)
        {
            case PowExpr { Base: var b, Exponent: var n } when n.FreeOf(x):
                {
                    var a = LinearSlope(b, x);
                    if (a is not null)
                        return n.IsNumber && n is Num { Value.IsMinusOne: true }
                            ? Expr.Call("log", b) / a
                            : Expr.Power(b, n + 1) / ((n + 1) * a);
                    // (L^m)^n with L linear, e.g. sqrt(x^2) = |x|: d/dx[L (L^m)^n] = a (m n + 1) (L^m)^n.
                    if (b is PowExpr { Base: var L, Exponent: Num m } && LinearSlope(L, x) is { } aL && n is Num nn)
                    {
                        var mn = m.Value * nn.Value;
                        if (mn != BigRational.MinusOne) return L * f / (new Num(mn + 1) * aL);
                    }
                    if (b is FnExpr fb && fb.Args.Count == 1 && LinearSlope(fb.Args[0], x) is { } a2)
                    {
                        var u = fb.Args[0];
                        if (n is Num { Value: var two } && two == 2)
                        {
                            switch (fb.Name)
                            {
                                case "sec": return Expr.Call("tan", u) / a2;
                                case "csc": return -Expr.Call("cot", u) / a2;
                                case "sech": return Expr.Call("tanh", u) / a2;
                                case "csch": return -Expr.Call("coth", u) / a2;
                            }
                        }
                        if (n is Num { Value.IsMinusOne: true })
                        {
                            switch (fb.Name)
                            {
                                case "cos": return Table(Expr.Call("sec", u), x);
                                case "sin": return Table(Expr.Call("csc", u), x);
                                case "tan": return Table(Expr.Call("cot", u), x);
                                case "cot": return Table(Expr.Call("tan", u), x);
                            }
                        }
                    }
                    return null;
                }
            case PowExpr { Base: var b, Exponent: var e } when b.FreeOf(x):
                {
                    var a = LinearSlope(e, x);
                    if (a is not null) return b == Expr.E ? f / a : f / (a * Expr.Call("log", b));
                    // Gaussian: e^(c x^2) with c < 0
                    if (b == Expr.E && PolyView.Coefficients(e, x) is { Count: 3 } c && c[1].IsZero && c[0].IsZero && (c[2].IsNegativeNumber || Engine.Assumptions.IsNegative(c[2])))
                    {
                        var k = -c[2];
                        return Expr.Sqrt(Expr.Pi) / (2 * Expr.Sqrt(k)) * Expr.Call("erf", Expr.Sqrt(k) * x);
                    }
                    return null;
                }
            case FnExpr fn when fn.Args.Count == 1:
                {
                    var u = fn.Args[0];
                    var a = LinearSlope(u, x);
                    if (a is null) return null;
                    Expr? F = fn.Name switch
                    {
                        "sin" => -Expr.Call("cos", u),
                        "cos" => Expr.Call("sin", u),
                        "tan" => -Expr.Call("log", Expr.Call("cos", u)),
                        "cot" => Expr.Call("log", Expr.Call("sin", u)),
                        "sec" => Expr.Call("log", Expr.Call("sec", u) + Expr.Call("tan", u)),
                        "csc" => -Expr.Call("log", Expr.Call("csc", u) + Expr.Call("cot", u)),
                        "sinh" => Expr.Call("cosh", u),
                        "cosh" => Expr.Call("sinh", u),
                        "tanh" => Expr.Call("log", Expr.Call("cosh", u)),
                        "coth" => Expr.Call("log", Expr.Call("sinh", u)),
                        "sech" => 2 * Expr.Call("atan", Expr.Call("tanh", u / 2)),
                        "log" => u * Expr.Call("log", u) - u,
                        "asin" => u * Expr.Call("asin", u) + Expr.Sqrt(1 - Expr.Power(u, 2)),
                        "acos" => u * Expr.Call("acos", u) - Expr.Sqrt(1 - Expr.Power(u, 2)),
                        "atan" => u * Expr.Call("atan", u) - Expr.Call("log", 1 + Expr.Power(u, 2)) / 2,
                        "acot" => u * Expr.Call("acot", u) + Expr.Call("log", 1 + Expr.Power(u, 2)) / 2,
                        "asinh" => u * Expr.Call("asinh", u) - Expr.Sqrt(Expr.Power(u, 2) + 1),
                        "acosh" => u * Expr.Call("acosh", u) - Expr.Sqrt(Expr.Power(u, 2) - 1),
                        "atanh" => u * Expr.Call("atanh", u) + Expr.Call("log", 1 - Expr.Power(u, 2)) / 2,
                        "erf" => u * Expr.Call("erf", u) + Expr.Exp(-Expr.Power(u, 2)) / Expr.Sqrt(Expr.Pi),
                        "abs" => u * Expr.Call("abs", u) / 2,
                        "sign" => Expr.Call("abs", u),
                        _ => null,
                    };
                    return F is null ? null : F / a;
                }
            case MulExpr { Factors.Count: 2 } m:
                {
                    // sec*tan, csc*cot
                    if (m.Factors[0] is FnExpr f1 && m.Factors[1] is FnExpr f2 && f1.Args.Count == 1 && f2.Args.Count == 1 && f1.Args[0] == f2.Args[0])
                    {
                        var u = f1.Args[0];
                        var a = LinearSlope(u, x);
                        if (a is null) return null;
                        var names = new HashSet<string> { f1.Name, f2.Name };
                        if (names.SetEquals(["sec", "tan"])) return Expr.Call("sec", u) / a;
                        if (names.SetEquals(["csc", "cot"])) return -Expr.Call("csc", u) / a;
                        if (names.SetEquals(["sech", "tanh"])) return -Expr.Call("sech", u) / a;
                    }
                    return null;
                }
        }
        return null;
    }

    // ---- rational functions --------------------------------------------------------------------------

    private static Expr? RationalIntegrate(Expr f, Expr x)
    {
        var (n, d) = Rational.NumDen(f);
        if (d.FreeOf(x)) return PolynomialIntegrate(f, x);
        var pn = PolyView.ToQPoly(n, x);
        var pd = PolyView.ToQPoly(d, x);
        if (pn is null || pd is null) return SymbolicRational(n, d, x);

        var (quot, rem) = pn.DivRem(pd);
        var result = new List<Expr> { PolynomialIntegrate(quot.ToExpr(x), x)! };
        if (rem.IsZero) return Expr.Plus(result);

        var (content, factors) = pd.Factor();
        // Decompose rem/pd into partial fractions and integrate each piece.
        var decomposition = factors.Count == 1 && factors[0].Multiplicity == 1
            ? rem.ToExpr(x) / pd.ToExpr(x)
            : Rational.PartialFractions(rem, pd, x);
        if (decomposition is null) return null;
        foreach (var term in decomposition is AddExpr a ? a.Terms : [decomposition])
        {
            var piece = PartialFractionTerm(term, x);
            if (piece is null) return null;
            result.Add(piece);
        }
        return Expr.Plus(result);
    }

    private static Expr? PolynomialIntegrate(Expr p, Expr x)
    {
        var c = PolyView.Coefficients(p, x);
        if (c is null) return null;
        return Expr.Plus(c.Select((k, i) => k * Expr.Power(x, i + 1) / (i + 1)));
    }

    /// <summary>Integrates A(x)/F(x)^k where F is linear or an irreducible quadratic and deg A &lt; deg F.</summary>
    private static Expr? PartialFractionTerm(Expr term, Expr x)
    {
        var (n, d) = Rational.NumDen(term);
        if (d.FreeOf(x)) return PolynomialIntegrate(term, x);
        // d = c * F^k
        var (dc, drest) = d.SplitCoefficient();
        Expr F = drest; int k = 1;
        if (drest is PowExpr { Exponent: Num { Value.IsInteger: true } kk } pw) { F = pw.Base; k = (int)kk.Value.Num; }
        var pF = PolyView.ToQPoly(F, x);
        var pN = PolyView.ToQPoly(n / dc, x);
        if (pF is null || pN is null) return null;

        if (pF.Degree == 1)
        {
            var A = pN[0];
            var slope = pF[1];
            if (k == 1) return new Num(A / slope) * Expr.Call("log", F);
            return new Num(A / slope) * Expr.Power(F, 1 - k) / (1 - k);
        }
        if (pF.Degree == 2)
        {
            // Normalise F = a(x^2 + p x + q).
            var a = pF[2];
            var p = pF[1] / a; var q = pF[0] / a;
            var scale = BigRational.Pow(a, k);
            var B = pN[1] / scale; var C = pN[0] / scale;
            var Q = Expr.Power(x, 2) + new Num(p) * x + new Num(q);
            var D = q - p * p / 4; // > 0 for irreducible quadratics
            Expr result = Expr.Zero;
            if (!B.IsZero)
            {
                result += k == 1
                    ? new Num(B / 2) * Expr.Call("log", Q)
                    : new Num(B / 2) * Expr.Power(Q, 1 - k) / (1 - k);
            }
            var c2 = C - B * p / 2;
            if (!c2.IsZero)
                result += new Num(c2) * QuadraticReduction(x + new Num(p / 2), new Num(D), k);
            return result;
        }
        if (k == 1) return ResidueLogs(pN, pF, x);
        return null;
    }

    /// <summary>
    /// ∫ A/F for an irreducible F of degree ≥ 3 whose roots α are known exactly: Σ A(α)/F'(α) log(x − α),
    /// with each conjugate pair α = a ± bi combined into u log((x − a)² + b²) − 2v atan((x − a)/b), where
    /// A(α)/F'(α) = u + iv.
    /// </summary>
    private static Expr? ResidueLogs(QPoly A, QPoly F, Expr x)
    {
        var roots = Solver.PolynomialRoots(F.ToExpr(x), x);
        if (roots is null || roots.Count != F.Degree || roots.Any(r => r.ContainsAny(z => z is Real))) return null;
        var dF = F.Derivative();
        var terms = new List<Expr>();
        var used = new bool[roots.Count];
        for (int i = 0; i < roots.Count; i++)
        {
            if (used[i]) continue;
            var parts = ComplexParts(roots[i]);
            if (parts is null) return null;
            var (a, b) = parts.Value;
            var num = ComplexParts(A.ToExpr(x).Substitute(x, roots[i]));
            var den = ComplexParts(dF.ToExpr(x).Substitute(x, roots[i]));
            if (num is null || den is null) return null;
            var (nr, ni) = num.Value;
            var (dr, di) = den.Value;
            var modulus = Expansion.Expand(dr * dr + di * di);
            var u = Simplifier.Simplify((nr * dr + ni * di) / modulus);
            var v = Simplifier.Simplify((ni * dr - nr * di) / modulus);
            used[i] = true;
            if (Simplifier.Simplify(b).IsZero)
            {
                terms.Add(u * Expr.Call("log", x - a));
                continue;
            }
            // Find and consume the conjugate root.
            int j = Enumerable.Range(0, roots.Count).FirstOrDefault(k => !used[k] && IsConjugate(roots[k], a, b), -1);
            if (j < 0) return null;
            used[j] = true;
            if (NumericEval.EvalReal(b) < 0) { b = -b; v = -v; }
            terms.Add(u * Expr.Call("log", Expansion.Expand(Expr.Power(x - a, 2)) + Simplifier.Simplify(b * b)));
            terms.Add(-2 * v * Expr.Call("atan", Simplifier.Simplify((x - a) / b)));
        }
        return Expr.Plus(terms);
    }

    private static bool IsConjugate(Expr root, Expr a, Expr b)
    {
        var p = ComplexParts(root);
        if (p is null) return false;
        try
        {
            return Math.Abs(NumericEval.EvalReal(p.Value.Re - a)) < 1e-12 && Math.Abs(NumericEval.EvalReal(p.Value.Im + b)) < 1e-12;
        }
        catch (MathException) { return false; }
    }

    /// <summary>Exact real and imaginary parts of a constant that is polynomial in I; null otherwise.</summary>
    private static (Expr Re, Expr Im)? ComplexParts(Expr z)
    {
        var e = Expansion.Expand(z);
        var c = PolyView.Coefficients(e, Expr.I);
        if (c is null || c.Any(k => !k.IsConstant || k.Contains(Expr.I))) return null;
        Expr re = Expr.Zero, im = Expr.Zero;
        for (int k = 0; k < c.Count; k++)
        {
            switch (k % 4)
            {
                case 0: re += c[k]; break;
                case 1: im += c[k]; break;
                case 2: re -= c[k]; break;
                default: im -= c[k]; break;
            }
        }
        // Parts must be real: reject nested radicals of negative numbers that hide an imaginary unit.
        try
        {
            var vr = NumericEval.Eval(re); var vi = NumericEval.Eval(im);
            if (Math.Abs(vr.Imaginary) > 1e-12 || Math.Abs(vi.Imaginary) > 1e-12) return null;
        }
        catch (MathException) { return null; }
        return (re, im);
    }

    // ---- rational functions of sin and cos ----------------------------------------------------------

    /// <summary>
    /// ∫ R(sin u, cos u) dx with u linear in x, by the substitution t = tan(u/2): sin u = 2t/(1 + t²),
    /// cos u = (1 − t²)/(1 + t²), du = 2 dt/(1 + t²). The result jumps where u ≡ π (mod 2π); definite
    /// integrals account for that by splitting at the jumps.
    /// </summary>
    private static Expr? Weierstrass(Expr f, Expr x, int depth)
    {
        var trig = f.Subexpressions().OfType<FnExpr>()
            .Where(g => g.Name is "sin" or "cos" or "tan" or "cot" or "sec" or "csc" && !g.Args[0].FreeOf(x))
            .ToList();
        if (trig.Count == 0) return null;
        var u = trig[0].Args[0];
        if (trig.Any(g => g.Args[0] != u)) return null;
        var slope = LinearSlope(u, x);
        if (slope is null) return null;
        var t = new Sym("\u0001wt" + depth);
        var one = Expr.One;
        var t2 = Expr.Power(t, 2);
        var map = new Dictionary<Expr, Expr>();
        foreach (var g in trig.Distinct())
        {
            map[g] = g.Name switch
            {
                "sin" => 2 * t / (one + t2),
                "cos" => (one - t2) / (one + t2),
                "tan" => 2 * t / (one - t2),
                "cot" => (one - t2) / (2 * t),
                "sec" => (one + t2) / (one - t2),
                _ => (one + t2) / (2 * t),
            };
        }
        var g2 = f.Substitute(map);
        if (!g2.FreeOf(x)) return null;
        var integrand = Rational.Cancel(g2 * 2 / (one + t2));
        var F = RationalIntegrate(integrand, t);
        if (F is null) return null;
        return F.Substitute(t, Expr.Call("tan", u / 2)) / slope;
    }

    /// <summary>I_k = ∫ dt/(t^2 + D)^k via the standard reduction formula.</summary>
    private static Expr QuadraticReduction(Expr t, Expr D, int k)
    {
        var sd = Expr.Sqrt(D);
        if (k == 1) return Expr.Call("atan", t / sd) / sd;
        var km1 = new Num(k - 1);
        return t / (2 * D * km1 * Expr.Power(Expr.Power(t, 2) + D, k - 1))
             + new Num(new BigRational(2 * k - 3, 2 * (k - 1))) / D * QuadraticReduction(t, D, k - 1);
    }

    /// <summary>Linear or quadratic denominators with symbolic coefficients (e.g. 1/(x^2 + a^2)).</summary>
    private static Expr? SymbolicRational(Expr n, Expr d, Expr x)
    {
        var cn = PolyView.Coefficients(n, x);
        var cd = PolyView.Coefficients(d, x);
        if (cn is null || cd is null) return null;
        if (cd.Count == 2 && cn.Count == 1)
            return cn[0] / cd[1] * Expr.Call("log", d);
        if (cd.Count == 3 && cn.Count <= 2)
        {
            var (a, b, c) = (cd[2], cd[1], cd[0]);
            var B = cn.Count == 2 ? cn[1] : Expr.Zero;
            var C = cn[0];
            // (B x + C)/(a x^2 + b x + c) = B/(2a) * (2a x + b)/Q + (C - B b/(2a))/Q
            Expr result = Expr.Zero;
            if (!B.IsZero) result += B / (2 * a) * Expr.Call("log", d);
            var rest = Solver.Tidy(C - B * b / (2 * a));
            if (!rest.IsZero)
            {
                var disc = Solver.Tidy(Expansion.Expand(4 * a * c - b * b));
                var s = Expr.Sqrt(disc);
                result += rest * 2 / s * Expr.Call("atan", Solver.Tidy((2 * a * x + b) / s));
            }
            return result;
        }
        return null;
    }

    // ---- substitution ------------------------------------------------------------------------------------

    private static Expr? Substitution(Expr f, Expr x, int depth)
    {
        var t = new Sym("\u0001u" + depth);
        var seen = new HashSet<Expr>();
        var candidates = new List<Expr>();
        foreach (var s in f.Subexpressions())
        {
            if (s == x || s.FreeOf(x)) continue;
            IEnumerable<Expr> cs = s switch
            {
                FnExpr fn => [fn, .. fn.Args],
                PowExpr p => [p, p.Base, p.Exponent],
                _ => [],
            };
            foreach (var c in cs)
                if (c != x && !c.FreeOf(x) && c != f && seen.Add(c)) candidates.Add(c);
        }
        foreach (var u in candidates.OrderBy(c => c.Complexity()).Take(20))
        {
            var du = Derivative.Diff(u, x);
            if (du.IsZero) continue;
            var q = f / du;
            var inT = ReplaceInTermsOf(q, u, x, t);
            if (inT is null) continue;
            var r = Integrate(inT, t, depth + 1);
            if (r is not null) return r.Substitute(t, u);
        }
        return null;
    }

    /// <summary>Rewrites q (a function of x) as a function of t where t = u(x); null if x remains.</summary>
    private static Expr? ReplaceInTermsOf(Expr q, Expr u, Expr x, Sym t)
    {
        var direct = q.Substitute(u, t);
        if (direct.FreeOf(x)) return direct;
        if (u is PowExpr { Base: var eb, Exponent: var ee } && eb.FreeOf(x))
        {
            // b^(k*e) -> t^k for numeric k (e.g. e^(-x) with t = e^x)
            var viaKernel = q.MapBottomUp(z =>
            {
                if (z is PowExpr pz && pz.Base == eb)
                {
                    var ratio = Rational.Cancel(pz.Exponent / ee);
                    if (ratio.IsNumber) return Expr.Power(t, ratio);
                }
                return z;
            });
            if (viaKernel.FreeOf(x)) return viaKernel;
        }
        var cancelled = Rational.Cancel(q).Substitute(u, t);
        if (cancelled.FreeOf(x)) return cancelled;
        // Express x through t by inverting u (works for powers and simple compositions).
        if (u is PowExpr { Base: var b, Exponent: Num } p && b == x)
        {
            var inv = Expr.Power(t, Expr.One / p.Exponent);
            var r = direct.Substitute(x, inv);
            // Reject results with fractional powers of t: they only hold on half the domain.
            if (r.FreeOf(x) && !r.ContainsAny(z => z is PowExpr { Exponent: Num { Value.IsInteger: false } } fp && !fp.Base.FreeOf(t)))
                return r;
        }
        var lin = PolyView.Coefficients(u, x);
        if (lin is { Count: 2 })
        {
            var r = direct.Substitute(x, (t - lin[0]) / lin[1]);
            if (r.FreeOf(x)) return Expansion.Expand(r);
        }
        return null;
    }

    // ---- trigonometric integrals ----------------------------------------------------------------------

    private static Expr? Trig(Expr f, Expr x, int depth)
    {
        var factors = f is MulExpr m ? m.Factors.ToList() : [f];
        // Collect sin^m cos^n with a common linear argument.
        Expr? arg = null;
        BigRational sinPow = 0, cosPow = 0, tanPow = 0, secPow = 0;
        var trigFactors = new List<(string Name, Expr Arg, BigRational Pow)>();
        foreach (var fac in factors)
        {
            var (b, e) = fac is PowExpr p ? (p.Base, p.Exponent) : (fac, Expr.One);
            if (b is FnExpr { Name: "sin" or "cos" or "tan" or "sec" or "csc" or "cot" } fn && e is Num { Value.IsInteger: true } k)
                trigFactors.Add((fn.Name, fn.Args[0], k.Value));
            else return null;
        }
        if (trigFactors.Count == 0) return null;

        // Products of sin/cos with different linear arguments: product-to-sum.
        if (trigFactors.Count == 2 && trigFactors.All(t => t.Pow.IsOne && t.Name is "sin" or "cos") && trigFactors[0].Arg != trigFactors[1].Arg)
        {
            var (n1, A, _) = trigFactors[0];
            var (n2, B, _) = trigFactors[1];
            Expr sum = n1 == n2
                ? n1 == "sin"
                    ? (Expr.Call("cos", A - B) - Expr.Call("cos", A + B)) / 2
                    : (Expr.Call("cos", A - B) + Expr.Call("cos", A + B)) / 2
                : n1 == "sin"
                    ? (Expr.Call("sin", A + B) + Expr.Call("sin", A - B)) / 2
                    : (Expr.Call("sin", A + B) + Expr.Call("sin", B - A)) / 2;
            return Integrate(Expansion.Expand(sum), x, depth + 1);
        }

        if (trigFactors.Select(t => t.Arg).Distinct().Count() != 1) return null;
        arg = trigFactors[0].Arg;
        if (LinearSlope(arg, x) is null) return null;
        foreach (var (name, _, pow) in trigFactors)
        {
            switch (name)
            {
                case "sin": sinPow += pow; break;
                case "cos": cosPow += pow; break;
                case "tan": tanPow += pow; break;
                case "sec": secPow += pow; break;
                case "csc": sinPow -= pow; break;
                case "cot": cosPow += pow; sinPow -= pow; break;
            }
        }
        var u = arg;
        Expr S = Expr.Call("sin", u), C = Expr.Call("cos", u);

        if (tanPow.Sign > 0 && sinPow.IsZero && cosPow.IsZero && secPow.IsZero && tanPow >= 2)
        {
            // tan^n = tan^(n-2) (sec^2 - 1)
            var n = (int)tanPow.Num;
            var T = Expr.Call("tan", u);
            var rewritten = Expr.Power(T, n - 2) * Expr.Power(Expr.Call("sec", u), 2) - Expr.Power(T, n - 2);
            return Integrate(rewritten, x, depth + 1);
        }
        if (secPow >= 3 && sinPow.IsZero && cosPow.IsZero && tanPow.IsZero)
        {
            // ∫sec^n = sec^(n-2) tan/(n-1) + (n-2)/(n-1) ∫sec^(n-2)
            var n = (int)secPow.Num;
            var a = LinearSlope(u, x)!;
            var lower = Integrate(Expr.Power(Expr.Call("sec", u), n - 2), x, depth + 1);
            if (lower is null) return null;
            return Expr.Power(Expr.Call("sec", u), n - 2) * Expr.Call("tan", u) / ((n - 1) * a) + new Num(new BigRational(n - 2, n - 1)) * lower;
        }
        if (!tanPow.IsZero || !secPow.IsZero)
        {
            // Rewrite in terms of sin and cos.
            sinPow += tanPow;
            cosPow -= tanPow + secPow;
            if (tanPow.IsZero && secPow.IsZero) return null;
            return Integrate(Expr.Power(S, new Num(sinPow)) * Expr.Power(C, new Num(cosPow)), x, depth + 1);
        }

        if (sinPow.Sign > 0 && !sinPow.Num.IsEven && sinPow > 1)
        {
            // sin^(2k+1) = sin (1 - cos^2)^k
            var k = (int)((sinPow - 1) / 2).Num;
            var rewritten = Expansion.Expand(S * Expr.Power(1 - Expr.Power(C, 2), k) * Expr.Power(C, new Num(cosPow)));
            return Integrate(rewritten, x, depth + 1);
        }
        if (cosPow.Sign > 0 && !cosPow.Num.IsEven && cosPow > 1)
        {
            var k = (int)((cosPow - 1) / 2).Num;
            var rewritten = Expansion.Expand(C * Expr.Power(1 - Expr.Power(S, 2), k) * Expr.Power(S, new Num(sinPow)));
            return Integrate(rewritten, x, depth + 1);
        }
        if (sinPow.Sign >= 0 && cosPow.Sign >= 0 && sinPow.Num.IsEven && cosPow.Num.IsEven && (sinPow + cosPow).Sign > 0)
        {
            // Power reduction.
            var C2 = Expr.Call("cos", 2 * u);
            var rewritten = Expansion.Expand(Expr.Power((1 - C2) / 2, new Num(sinPow / 2)) * Expr.Power((1 + C2) / 2, new Num(cosPow / 2)));
            return Integrate(rewritten, x, depth + 1);
        }
        return null;
    }

    // ---- integration by parts ---------------------------------------------------------------------------

    private static bool IsPolynomialFactor(Expr e, Expr x) =>
        e == x || (e is PowExpr { Base: var b, Exponent: Num { Value.IsInteger: true, Value.Sign: > 0 } } && PolyView.IsPolynomial(b, x)) || (e is AddExpr && PolyView.IsPolynomial(e, x));

    private static bool IsLogLike(Expr e) =>
        (e is PowExpr { Base: FnExpr inner, Exponent: Num { Value.IsInteger: true, Value.Sign: > 0 } } && IsLogLike(inner)) ||
        e is FnExpr { Name: "log" or "asin" or "acos" or "atan" or "acot" or "asinh" or "acosh" or "atanh" };

    private static Expr? ByParts(Expr f, Expr x, int depth)
    {
        var factors = f is MulExpr m ? m.Factors.ToList() : [f];
        var poly = factors.Where(t => IsPolynomialFactor(t, x)).ToList();
        var rest = factors.Where(t => !IsPolynomialFactor(t, x)).ToList();
        if (rest.Count == 0) return null;
        var P = Expr.Times(poly);
        var R = Expr.Times(rest);

        // e^(a x) * sin/cos(b x): closed form.
        if (poly.Count == 0 && rest.Count == 2)
        {
            var cyc = ExpTrig(rest[0], rest[1], x) ?? ExpTrig(rest[1], rest[0], x);
            if (cyc is not null) return cyc;
        }

        if (rest.Count == 1 && IsLogLike(R))
        {
            // u = R, dv = P dx
            var V = Integrate(P, x, depth + 1);
            if (V is null) return null;
            var inner = Integrate(Rational.Cancel(Expansion.Expand(V * Derivative.Diff(R, x))), x, depth + 1);
            return inner is null ? null : R * V - inner;
        }

        if (poly.Count > 0)
        {
            // u = P, dv = R dx
            var V = Integrate(R, x, depth + 1);
            if (V is null) return null;
            var dP = Derivative.Diff(P, x);
            var inner = Integrate(Expansion.Expand(dP * V), x, depth + 1);
            return inner is null ? null : P * V - inner;
        }
        return null;
    }

    private static Expr? ExpTrig(Expr e, Expr t, Expr x)
    {
        if (e is not PowExpr { Base: Sym { Name: "E" }, Exponent: var ex }) return null;
        if (t is not FnExpr { Name: "sin" or "cos" } tf) return null;
        var a = LinearSlope(ex, x);
        var b = LinearSlope(tf.Args[0], x);
        if (a is null || b is null) return null;
        var u = tf.Args[0];
        var den = a * a + b * b;
        return tf.Name == "sin"
            ? e * (a * Expr.Call("sin", u) - b * Expr.Call("cos", u)) / den
            : e * (a * Expr.Call("cos", u) + b * Expr.Call("sin", u)) / den;
    }

    // ---- quadratic radicals ---------------------------------------------------------------------------

    private static Expr? QuadraticRadical(Expr f, Expr x)
    {
        // Matches Q^(1/2) or Q^(-1/2) with Q quadratic in x (numeric or symbolic coefficients).
        if (f is not PowExpr { Base: var Q, Exponent: Num { Value: var e } }) return null;
        if (e != BigRational.Half && e != -BigRational.Half) return null;
        var c = PolyView.Coefficients(Q, x);
        if (c is null || c.Count != 3) return null;
        var (A, B, C) = (c[2], c[1], c[0]);
        Expr inverseSqrt;
        bool negative = A.IsNegativeNumber || (A.IsConstant && !A.Contains(Expr.I) && NumericEval.EvalReal(A) < 0) || Engine.Assumptions.IsNegative(A);
        if (negative)
        {
            var disc = Solver.Tidy(Expansion.Expand(B * B - 4 * A * C));
            inverseSqrt = -Expr.Call("asin", Solver.Tidy((2 * A * x + B) / Expr.Sqrt(disc))) / Expr.Sqrt(-A);
        }
        else if (B.IsZero)
        {
            inverseSqrt = Expr.Call("log", Expr.Sqrt(A) * x + Expr.Sqrt(Q)) / Expr.Sqrt(A);
        }
        else
        {
            inverseSqrt = Expr.Call("log", 2 * Expr.Sqrt(A) * Expr.Sqrt(Q) + 2 * A * x + B) / Expr.Sqrt(A);
        }
        if (e < 0) return inverseSqrt;
        // ∫ sqrt(Q) = (2Ax + B) sqrt(Q)/(4A) + (4AC - B^2)/(8A) ∫ 1/sqrt(Q)
        return (2 * A * x + B) * Expr.Sqrt(Q) / (4 * A) + Solver.Tidy(Expansion.Expand(4 * A * C - B * B)) / (8 * A) * inverseSqrt;
    }

    // ---- definite integrals ---------------------------------------------------------------------------

    public static Expr Definite(Expr f, Expr x, Expr a, Expr b) =>
        Engine.Assumptions.WithVariable(x, "real", () => DefiniteCore(Engine.Assumptions.Refine(f), x, a, b));

    private static Expr DefiniteCore(Expr f, Expr x, Expr a, Expr b)
    {
        bool numericOnly = f.FreeSymbols().All(s => s == x) && a.IsConstant && b.IsConstant;
        (double Value, double Error)? numeric = null;
        double lo = double.NaN, hi = double.NaN;
        if (numericOnly)
        {
            try
            {
                lo = Bound(a); hi = Bound(b);
                // A non-integrable singularity makes quadrature and F(b) - F(a) both meaningless (∫ 1/x over [-1, 1]).
                var cuts = CheckSingularities(f, (Sym)x, Math.Min(lo, hi), Math.Max(lo, hi));
                var fn = NumericEval.Compile1(f, ((Sym)x).Name);
                numeric = PiecewiseQuadrature(fn, lo, hi, cuts);
            }
            catch (MathException ex) when (ex is not DivergenceException) { }
        }

        var F = Antiderivative(f, x);
        if (F is not null && !HasRealCoefficients(f))
        {
            var upper = Limits.Limit(F, x, b, -1);
            var lower = Limits.Limit(F, x, a, +1);
            var value = Simplifier.Simplify(upper - lower);
            if (!value.IsUndefinedOrInfinite || value.IsInfinite)
            {
                if (numeric is null) return value;
                var (nv, err) = numeric.Value;
                if (value.IsInfinite) return value;
                if (Agrees(value, nv, err)) return value;
            }
            // F may jump inside the interval (e.g. antiderivatives from the tan(x/2) substitution): split there.
            if (numeric is { } target && double.IsFinite(lo) && double.IsFinite(hi) && SplitAtJumps(F, (Sym)x, a, b, lo, hi) is { } split
                && Agrees(split, target.Value, target.Error))
                return split;
        }
        if (numeric is { } nres)
        {
            if (!double.IsFinite(nres.Value) || nres.Error > 1e-3 * (1 + Math.Abs(nres.Value)))
                throw new MathException("The integral does not appear to converge.");
            return new Real(nres.Value);
        }
        if (F is null) return FnExpr.Inert("integrate", f, x, a, b);
        return Simplifier.Simplify(Limits.Limit(F, x, b, -1) - Limits.Limit(F, x, a, +1));
    }

    private static bool HasRealCoefficients(Expr e) => e.ContainsAny(z => z is Real);

    private static double Bound(Expr e)
    {
        if (e == Expr.Infinity) return double.PositiveInfinity;
        if (e.IsInfinite && e.HasNegativeCoefficient) return double.NegativeInfinity;
        var v = NumericEval.EvalReal(e);
        if (double.IsNaN(v)) throw new MathException("Integration limits must be real.");
        return v;
    }

    private static bool Agrees(Expr value, double nv, double err)
    {
        try
        {
            var check = NumericEval.Eval(value);
            return Math.Abs(check.Imaginary) < 1e-9 * (1 + Math.Abs(nv)) && Math.Abs(check.Real - nv) <= 1e-6 * (1 + Math.Abs(nv)) && err < 1e-6 * (1 + Math.Abs(nv));
        }
        catch (MathException) { return false; }
    }

    /// <summary>Thrown when an integral is known to diverge.</summary>
    public sealed class DivergenceException() : MathException("The integral diverges.");

    /// <summary>
    /// Finds the points of [lo, hi] where f may be singular (zeros of denominators, of log arguments and of
    /// the cos/sin behind tan, sec, cot, csc) and throws when f is not integrable at one of them.
    /// </summary>
    private static List<double> CheckSingularities(Expr f, Sym x, double lo, double hi)
    {
        var suspects = new List<Expr>();
        var (_, den) = Rational.NumDen(Rational.Together(f));
        if (!den.FreeOf(x)) suspects.Add(den);
        foreach (var s in f.Subexpressions())
        {
            switch (s)
            {
                case FnExpr { Name: "log" } lg when !lg.Args[0].FreeOf(x): suspects.Add(lg.Args[0]); break;
                case FnExpr { Name: "tan" or "sec" } t when !t.Args[0].FreeOf(x): suspects.Add(Expr.Call("cos", t.Args[0])); break;
                case FnExpr { Name: "cot" or "csc" } t when !t.Args[0].FreeOf(x): suspects.Add(Expr.Call("sin", t.Args[0])); break;
                case PowExpr { Exponent: var e } p when e.IsNegativeNumber && !p.Base.FreeOf(x): suspects.Add(p.Base); break;
            }
        }
        var fn = NumericEval.Compile1(f, x.Name);
        var interior = new List<double>();
        foreach (var g in suspects.Distinct())
            foreach (var c in RealZeros(g, x, lo, hi))
            {
                if (!Integrable(fn, c, lo, hi)) throw new DivergenceException();
                if (c > lo && c < hi && !interior.Any(p => Math.Abs(p - c) < 1e-12 * (1 + Math.Abs(c)))) interior.Add(c);
            }
        interior.Sort();
        return interior;
    }

    /// <summary>Quadrature split at interior singular points, so that each piece only has endpoint singularities.</summary>
    private static (double Value, double Error) PiecewiseQuadrature(Func<double, double> fn, double lo, double hi, List<double> cuts)
    {
        if (cuts.Count == 0 || !double.IsFinite(lo) || !double.IsFinite(hi)) return NumericMethods.Integrate(fn, lo, hi);
        var points = new List<double> { Math.Min(lo, hi) };
        points.AddRange(cuts);
        points.Add(Math.Max(lo, hi));
        double value = 0, error = 0;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            // Each piece has a singular endpoint: tanh-sinh handles those to full precision.
            var (v, e) = NumericMethods.TanhSinh(fn, points[i], points[i + 1]);
            if (!double.IsFinite(v) || e > 1e-8 * (1 + Math.Abs(v))) (v, e) = NumericMethods.Integrate(fn, points[i], points[i + 1]);
            value += v; error += e;
        }
        return lo <= hi ? (value, error) : (-value, error);
    }

    private static IEnumerable<double> RealZeros(Expr g, Sym x, double lo, double hi)
    {
        var found = new List<double>();
        bool exact = false;
        try
        {
            var sols = Solver.SolveFor(g, x, out var numeric);
            exact = !numeric;
            foreach (var s in sols)
            {
                if (!s.IsConstant) continue;
                var v = NumericEval.Eval(s);
                if (Math.Abs(v.Imaginary) < 1e-12 * (1 + Math.Abs(v.Real))) found.Add(v.Real);
            }
        }
        catch (MathException) { }
        // Periodic zeros (cos(x) = 0) are only reported once by the solver; scan finite intervals as well.
        if ((!exact || g.ContainsAny(z => z is FnExpr { Name: "sin" or "cos" })) && double.IsFinite(lo) && double.IsFinite(hi))
        {
            try { found.AddRange(NumericMethods.RealRoots(NumericEval.Compile1(g, x.Name), lo, hi, 4000)); }
            catch (MathException) { }
        }
        return found.Where(v => v >= lo - 1e-12 && v <= hi + 1e-12);
    }

    /// <summary>Heuristic: f is integrable at c when h·|f(c ± h)| tends to zero.</summary>
    private static bool Integrable(Func<double, double> fn, double c, double lo, double hi)
    {
        double scale = Math.Max(1, Math.Abs(c));
        foreach (var side in new[] { -1, 1 })
        {
            if ((side < 0 && c <= lo) || (side > 0 && c >= hi)) continue;
            double G(double h) => h * Math.Abs(fn(c + side * h * scale));
            double g4 = G(1e-4), g6 = G(1e-6), g8 = G(1e-8);
            if (double.IsNaN(g4) || double.IsNaN(g6) || double.IsNaN(g8)) continue;
            if (double.IsInfinity(g8) || (g8 > 1e-12 && g8 > 0.3 * g6 && g6 > 0.3 * g4)) return false;
        }
        return true;
    }

    /// <summary>
    /// Locates jump discontinuities of F in (a, b), identifies them exactly and sums F over the pieces with
    /// one-sided limits. Null when a jump cannot be identified.
    /// </summary>
    private static Expr? SplitAtJumps(Expr F, Sym x, Expr a, Expr b, double lo, double hi)
    {
        Func<double, double> Fn;
        try { Fn = NumericEval.Compile1(F, x.Name); }
        catch (MathException) { return null; }
        const int N = 2000;
        var t = new double[N + 1];
        var v = new double[N + 1];
        for (int i = 0; i <= N; i++) { t[i] = lo + (hi - lo) * i / N; v[i] = Fn(t[i]); }
        var diffs = new List<double>();
        for (int i = 0; i < N; i++) if (double.IsFinite(v[i]) && double.IsFinite(v[i + 1])) diffs.Add(Math.Abs(v[i + 1] - v[i]));
        if (diffs.Count < N / 2) return null;
        diffs.Sort();
        double typical = diffs[diffs.Count / 2] + 1e-12;

        var cuts = new List<Expr>();
        for (int i = 0; i < N; i++)
        {
            if (!double.IsFinite(v[i]) || !double.IsFinite(v[i + 1])) continue;
            if (Math.Abs(v[i + 1] - v[i]) < 50 * typical || Math.Abs(v[i + 1] - v[i]) < 1e-6) continue;
            double l = t[i], r = t[i + 1], fl = v[i];
            for (int k = 0; k < 60; k++)
            {
                double m = (l + r) / 2, fm = Fn(m);
                if (Math.Abs(fm - fl) < Math.Abs(v[i + 1] - v[i]) / 2) { l = m; fl = fm; } else r = m;
            }
            var c = Limits.Recognize((l + r) / 2);
            if (c is Real) return null;
            if (!cuts.Contains(c)) cuts.Add(c);
            if (cuts.Count > 20) return null;
        }
        if (cuts.Count == 0) return null;
        var points = new List<Expr> { a };
        points.AddRange(lo <= hi ? cuts : Enumerable.Reverse(cuts));
        points.Add(b);
        var pieces = new List<Expr>();
        for (int i = 0; i + 1 < points.Count; i++)
        {
            int dirHi = lo <= hi ? -1 : 1;
            var up = Limits.Limit(F, x, points[i + 1], dirHi);
            var down = Limits.Limit(F, x, points[i], -dirHi);
            if (up.IsUndefinedOrInfinite || down.IsUndefinedOrInfinite) return null;
            pieces.Add(up - down);
        }
        return Simplifier.Simplify(Expr.Plus(pieces));
    }
}
