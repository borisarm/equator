namespace Equator.Core.Algebra;

/// <summary>Heuristic simplification: tries several equivalent forms and keeps the least complex one.</summary>
public static class Simplifier
{
    public static Expr Simplify(Expr e)
    {
        if (e is ListExpr or EqExpr) return e.WithArgs(e.Args.Select(Simplify).ToArray());
        e = SimplifyArguments(e);
        return Best(Candidates(e));
    }

    private static Expr SimplifyArguments(Expr e) => e switch
    {
        FnExpr f => f.WithArgs(f.Args.Select(Simplify).ToArray()),
        AddExpr or MulExpr or PowExpr => e.WithArgs(e.Args.Select(SimplifyArguments).ToArray()),
        _ => e,
    };

    private static Expr Best(IEnumerable<Expr> candidates)
    {
        Expr? best = null;
        int bestScore = int.MaxValue;
        foreach (var c in candidates)
        {
            int score = Score(c);
            if (score < bestScore) { best = c; bestScore = score; }
        }
        return best!;
    }

    private static int Score(Expr e)
    {
        // Node count, with a small penalty for negative signs inside sums and for nested powers.
        int s = e.Complexity() * 4;
        foreach (var sub in e.Subexpressions())
        {
            if (sub is AddExpr a) s += a.Terms.Count(t => t.HasNegativeCoefficient);
            if (sub is FnExpr) s += 1;
            if (sub is PowExpr { Exponent: var ex } && ex.IsNegativeNumber) s += 2;
        }
        return s;
    }

    /// <summary>Candidates larger than this many nodes are not transformed further (expansion can explode).</summary>
    private const int SizeBudget = 600;

    private static IEnumerable<Expr> Candidates(Expr e)
    {
        yield return e;
        if (e.IsNumber || e is Sym) yield break;
        Engine.Interrupt.Check();

        if (e.ContainsAny(z => z is PowExpr { Exponent: Num { Value.IsInteger: false } }))
        {
            var denested = Safe(() => Radicals.Denest(e));
            if (denested is not null && denested != e) { yield return denested; e = denested; }
            var rationalized = Safe(() => RationalizeDenominator(e));
            if (rationalized is not null) yield return rationalized;
            var combined = Safe(() => CombineRadicals(e));
            if (combined is not null && combined != e) { yield return combined; e = combined; }
        }

        if (e.Contains(Expr.I))
        {
            var normal = Safe(() => RationalizeComplex(e));
            if (normal is not null) yield return normal;
        }

        var expanded = Safe(() => Expansion.Expand(e));
        if (expanded is not null) yield return expanded;
        // Expanding can blow up (cubes of long sums); only cheap transformations beyond this point.
        if (expanded is not null && expanded.Complexity() > SizeBudget && expanded.Complexity() > 4 * e.Complexity())
        {
            var factoredOnly = Safe(() => Factoring.Factor(e));
            if (factoredOnly is not null) yield return factoredOnly;
            yield break;
        }

        var (_, den) = Rational.NumDen(e);
        Expr? cancelled = null;
        if (!den.IsOne)
        {
            cancelled = Safe(() => Rational.Cancel(e));
            if (cancelled is not null)
            {
                yield return cancelled;
                var fc = Safe(() => Factoring.Factor(cancelled));
                if (fc is not null) yield return fc;
            }
            var together = Safe(() => Rational.Together(e));
            if (together is not null) yield return together;
        }

        var factored = Safe(() => Factoring.Factor(e));
        if (factored is not null) yield return factored;

        if (e.ContainsAny(IsTrig))
            foreach (var t in TrigCandidates(e))
                yield return t;

        if (e.ContainsAny(x => x is FnExpr { Name: "log" }))
        {
            var combined = Safe(() => CombineLogs(expanded ?? e));
            if (combined is not null) yield return combined;
            if (Engine.Assumptions.Active)
            {
                var split = Safe(() => Expansion.Expand(ExpandLogs(e)));
                if (split is not null) yield return split;
            }
        }
    }

    private static Expr? Safe(Func<Expr> f)
    {
        try { return f(); }
        catch (MathException) { return null; }
        catch (DivideByZeroException) { return null; }
    }

    /// <summary>
    /// n / (a + b sqrt(r)) → n (a − b sqrt(r)) / (a² − b² r) for a denominator with a single square root of a
    /// rational number, e.g. 1/(1 + sqrt(2)) → sqrt(2) − 1.
    /// </summary>
    private static Expr? RationalizeDenominator(Expr e)
    {
        var (n, d) = Rational.NumDen(e);
        d = Expansion.Expand(d);
        if (d is not AddExpr sum) return null;
        var radicals = sum.Terms.SelectMany(t => t.Subexpressions()).OfType<PowExpr>()
            .Where(p => p.Base is Num && p.Exponent is Num { Value: var h } && h == BigRational.Half).Distinct().ToList();
        if (radicals.Count != 1) return null;
        var s = radicals[0];
        var c = PolyView.Coefficients(d, s);
        if (c is null || c.Count != 2 || !c.All(k => k.IsNumber)) return null;
        var conjugate = c[0] - c[1] * s;
        var newDen = Expansion.Expand(d * conjugate);
        if (!newDen.IsNumber || newDen.IsZero) return null;
        return Expansion.Expand(n * conjugate) / newDen;
    }

    /// <summary>
    /// n / d with i in d → n·conj(d) / (d·conj(d)), so (2 + i)/(3 − 4i) becomes 2/25 + 11/25 i. Only when the new
    /// denominator is free of i (conjugating i ↦ −i is the complex conjugate when the other symbols are real).
    /// </summary>
    private static Expr? RationalizeComplex(Expr e)
    {
        var (n, d) = Rational.NumDen(e);
        if (!d.Contains(Expr.I)) return null;
        var conjugate = d.Substitute(Expr.I, -Expr.I);
        var newDen = Expansion.Expand(d * conjugate);
        if (newDen.Contains(Expr.I) || newDen.IsZero) return null;
        return Expansion.Expand(Expansion.Expand(n * conjugate) / newDen);
    }

    /// <summary>
    /// Products of roots of positive rationals with the same index become one root, reduced: 3^(2/3)·6^(1/3) =
    /// 54^(1/3) = 3·2^(1/3), and 125^(1/4)·5^(-1/4) = 25^(1/4) = √5.
    /// </summary>
    private static Expr CombineRadicals(Expr e) => e.MapBottomUp(z =>
    {
        if (z is PowExpr { Base: Num { Value.Sign: > 0 } b, Exponent: Num { Value: var q } } single && !q.IsInteger)
            return Root(BigRational.Pow(b.Value, (int)q.Num), q.Den);
        if (z is not MulExpr m) return z;
        var groups = m.Factors
            .Where(f => f is PowExpr { Base: Num { Value.Sign: > 0 }, Exponent: Num { Value.IsInteger: false } })
            .Cast<PowExpr>()
            .GroupBy(p => ((Num)p.Exponent).Value.Den)
            .Where(g => g.Count() > 1)
            .ToList();
        if (groups.Count == 0) return z;
        var merged = groups.SelectMany(g => g).ToHashSet();
        var factors = m.Factors.Where(f => f is not PowExpr p || !merged.Contains(p)).ToList();
        foreach (var g in groups)
        {
            var value = g.Aggregate(BigRational.One, (acc, p) => acc * BigRational.Pow(((Num)p.Base).Value, (int)((Num)p.Exponent).Value.Num));
            factors.Add(Root(value, g.Key));
        }
        return Expr.Times(factors);
    });

    /// <summary>value^(1/q) with the index lowered when value is a perfect power (25^(1/4) = 5^(1/2)).</summary>
    private static Expr Root(BigRational value, System.Numerics.BigInteger q)
    {
        if (q > 64) return Expr.Power(new Num(value), new Num(new BigRational(1, q)));
        int index = (int)q;
        for (int k = index; k >= 2; k--)
        {
            if (index % k != 0) continue;
            if (IntMath.TryExactRoot(value.Num, k, out var rn) && IntMath.TryExactRoot(value.Den, k, out var rd))
                return Expr.Power(new Num(new BigRational(rn, rd)), new Num(new BigRational(1, index / k)));
        }
        return Expr.Power(new Num(value), new Num(new BigRational(1, index)));
    }

    private static bool IsTrig(Expr e) => e is FnExpr { Name: "sin" or "cos" or "tan" or "cot" or "sec" or "csc" or "sinh" or "cosh" or "tanh" };

    // ---- trigonometric simplification -------------------------------------------------------

    public static Expr TrigSimplify(Expr e) => Best(TrigCandidates(e).Prepend(e));

    private static IEnumerable<Expr> TrigCandidates(Expr e)
    {
        var basic = Safe(() => ToSinCos(e));
        if (basic is null) yield break;
        var cands = new List<Expr> { basic };
        var together = Safe(() => Rational.Cancel(basic));
        if (together is not null) cands.Add(together);

        foreach (var c in cands.ToList())
        {
            var s = Safe(() => Rational.Cancel(Expansion.Expand(ReplaceSquares(c, "sin", "cos", -1))));
            if (s is not null) cands.Add(s);
            var k = Safe(() => Rational.Cancel(Expansion.Expand(ReplaceSquares(c, "cos", "sin", -1))));
            if (k is not null) cands.Add(k);
            var h = Safe(() => Rational.Cancel(Expansion.Expand(ReplaceSquares(c, "cosh", "sinh", 1))));
            if (h is not null) cands.Add(h);
        }
        foreach (var c in cands.ToList())
        {
            var d = Safe(() => DoubleAngle(c));
            if (d is not null && d != c) cands.Add(d);
            var f = Safe(() => Factoring.Factor(c));
            if (f is not null) cands.Add(f);
        }
        // Angle-addition expansion: sin(x + y) − sin x cos y − cos x sin y → 0.
        if (basic.ContainsAny(z => z is FnExpr { Name: "sin" or "cos" } fz && fz.Args[0] is AddExpr))
        {
            var angles = Safe(() => Expansion.Expand(ExpandAngles(basic)));
            if (angles is not null)
            {
                cands.Add(angles);
                var ac = Safe(() => Rational.Cancel(Expansion.Expand(ReplaceSquares(angles, "sin", "cos", -1))));
                if (ac is not null) cands.Add(ac);
            }
        }
        var reduced = Safe(() => TrigReduce(basic));
        if (reduced is not null)
        {
            cands.Add(reduced);
            var rf = Safe(() => Factoring.Factor(reduced));
            if (rf is not null) cands.Add(rf);
        }
        foreach (var c in cands) yield return c;
        foreach (var c in cands) yield return FromSinCos(c);
    }

    /// <summary>Product-to-sum: rewrites products and powers of sin/cos as sums of sin/cos of combined angles.</summary>
    public static Expr TrigReduce(Expr e)
    {
        for (int iter = 0; iter < 12; iter++)
        {
            var ex = Expansion.Expand(e);
            var next = Expansion.Expand(ex.MapBottomUp(ReduceProduct));
            if (next == ex) return next;
            e = next;
        }
        return e;
    }

    private static Expr ReduceProduct(Expr x)
    {
        if (x is not MulExpr and not PowExpr) return x;
        var factors = x is MulExpr m ? m.Factors.ToList() : [x];
        var units = new List<FnExpr>();
        var rest = new List<Expr>();
        foreach (var f in factors)
        {
            if (f is FnExpr { Name: "sin" or "cos" } fn) units.Add(fn);
            else if (f is PowExpr { Base: FnExpr { Name: "sin" or "cos" } pf, Exponent: Num { Value.IsInteger: true } n } && n.Value.Sign > 0 && n.Value.Num <= 12)
                for (int i = 0; i < (int)n.Value.Num; i++) units.Add(pf);
            else rest.Add(f);
        }
        if (units.Count < 2) return x;
        var (a, b) = (units[0], units[1]);
        Expr A = a.Args[0], B = b.Args[0];
        Expr combined = (a.Name, b.Name) switch
        {
            ("sin", "sin") => (Expr.Call("cos", A - B) - Expr.Call("cos", A + B)) / 2,
            ("cos", "cos") => (Expr.Call("cos", A - B) + Expr.Call("cos", A + B)) / 2,
            ("sin", "cos") => (Expr.Call("sin", A + B) + Expr.Call("sin", A - B)) / 2,
            _ => (Expr.Call("sin", A + B) + Expr.Call("sin", B - A)) / 2,
        };
        return Expr.Times(rest.Concat(units.Skip(2)).Append(combined));
    }

    /// <summary>sin(a + b) → sin a cos b + cos a sin b and cos(a + b) → cos a cos b − sin a sin b, recursively.</summary>
    public static Expr ExpandAngles(Expr e) => e.MapBottomUp(z =>
    {
        if (z is FnExpr { Name: "sin" or "cos" } f && f.Args[0] is AddExpr s && s.Terms.Count <= 6)
        {
            var a = s.Terms[0];
            var b = Expr.Plus(s.Terms.Skip(1));
            Expr Sin(Expr u) => ExpandAngles(Expr.Call("sin", u));
            Expr Cos(Expr u) => ExpandAngles(Expr.Call("cos", u));
            return f.Name == "sin" ? Sin(a) * Cos(b) + Cos(a) * Sin(b) : Cos(a) * Cos(b) - Sin(a) * Sin(b);
        }
        return z;
    });

    /// <summary>tan -> sin/cos, sec -> 1/cos, etc.</summary>
    private static Expr ToSinCos(Expr e) => e.MapBottomUp(x => x switch
    {
        FnExpr { Name: "tan" } f => Expr.Call("sin", f.Args[0]) / Expr.Call("cos", f.Args[0]),
        FnExpr { Name: "cot" } f => Expr.Call("cos", f.Args[0]) / Expr.Call("sin", f.Args[0]),
        FnExpr { Name: "sec" } f => Expr.One / Expr.Call("cos", f.Args[0]),
        FnExpr { Name: "csc" } f => Expr.One / Expr.Call("sin", f.Args[0]),
        FnExpr { Name: "tanh" } f => Expr.Call("sinh", f.Args[0]) / Expr.Call("cosh", f.Args[0]),
        _ => x,
    });

    /// <summary>sin(u)/cos(u) -> tan(u) in products.</summary>
    private static Expr FromSinCos(Expr e) => e.MapBottomUp(x =>
    {
        if (x is not MulExpr m) return x;
        foreach (var f in m.Factors)
        {
            if (f is FnExpr { Name: "sin" } s)
            {
                var cosInv = Expr.Power(Expr.Call("cos", s.Args[0]), Expr.MinusOne);
                if (m.Factors.Contains(cosInv))
                    return x * Expr.Call("cos", s.Args[0]) / s * Expr.Call("tan", s.Args[0]);
            }
        }
        return x;
    });

    /// <summary>Replaces f(u)^n (n >= 2) by f(u)^(n mod 2) * (1 + sign * g(u)^2)^(n div 2), using f^2 = 1 + sign*g^2.</summary>
    private static Expr ReplaceSquares(Expr e, string f, string g, int sign) => e.MapBottomUp(x =>
    {
        if (x is PowExpr { Base: FnExpr fn, Exponent: Num { Value.IsInteger: true } n } && fn.Name == f && n.Value.Num >= 2)
        {
            var u = fn.Args[0];
            var k = n.Value.Num;
            Expr rest = k.IsEven ? Expr.One : fn;
            return rest * Expr.Power(Expr.One + new Num(sign) * Expr.Power(Expr.Call(g, u), 2), new Num(k / 2));
        }
        return x;
    });

    /// <summary>2 sin(u) cos(u) -> sin(2u) and cos(u)^2 - sin(u)^2 -> cos(2u).</summary>
    private static Expr DoubleAngle(Expr e) => e.MapBottomUp(x =>
    {
        if (x is MulExpr m)
        {
            var sin = m.Factors.OfType<FnExpr>().FirstOrDefault(f => f.Name == "sin");
            if (sin is not null && m.Factors.Contains(Expr.Call("cos", sin.Args[0])))
            {
                var u = sin.Args[0];
                return x / (sin * Expr.Call("cos", u)) * Expr.Call("sin", 2 * u) / 2;
            }
        }
        if (x is AddExpr a)
        {
            // a cos(u)^2 - a/2 -> (a/2) cos(2u);  a sin(u)^2 - a/2 -> -(a/2) cos(2u)
            foreach (var t in a.Terms)
            {
                var (c, rest) = t.SplitCoefficient();
                if (rest is PowExpr { Base: FnExpr { Name: "cos" or "sin" } tf, Exponent: Num { Value: var sq } } && sq == 2 && c is Num cn)
                {
                    var half = new Num(cn.Value / 2);
                    if (a.Terms.Contains(-half))
                    {
                        var c2 = Expr.Call("cos", 2 * tf.Args[0]);
                        return x - t + half + (tf.Name == "cos" ? half * c2 : -half * c2);
                    }
                }
            }
            foreach (var t in a.Terms)
            {
                var (c, rest) = t.SplitCoefficient();
                if (rest is PowExpr { Base: FnExpr { Name: "cos" } cf, Exponent: Num { Value: var two } } && two == 2)
                {
                    var u = cf.Args[0];
                    var partner = -c * Expr.Power(Expr.Call("sin", u), 2);
                    if (a.Terms.Contains(partner))
                        return x - t - partner + c * Expr.Call("cos", 2 * u);
                }
            }
        }
        return x;
    });

    // ---- logarithms -----------------------------------------------------------------------------

    /// <summary>
    /// log(a b) → log a + log b and log(a^r) → r log a, where the assumptions show the factors are positive
    /// (both identities fail for general complex arguments).
    /// </summary>
    public static Expr ExpandLogs(Expr e) => e.MapBottomUp(z =>
    {
        if (z is not FnExpr { Name: "log" } lg) return z;
        var arg = lg.Args[0];
        if (arg is MulExpr m)
        {
            var positive = m.Factors.Where(Engine.Assumptions.IsPositive).ToList();
            if (positive.Count == 0) return z;
            var rest = Expr.Times(m.Factors.Where(f => !positive.Contains(f)));
            var parts = positive.Select(f => ExpandLogs(Expr.Call("log", f))).ToList();
            if (!rest.IsOne) parts.Add(Expr.Call("log", rest));
            return Expr.Plus(parts);
        }
        if (arg is PowExpr p && Engine.Assumptions.IsPositive(p.Base) && Engine.Assumptions.IsReal(p.Exponent))
            return p.Exponent * ExpandLogs(Expr.Call("log", p.Base));
        return z;
    });

    /// <summary>log(a) + log(b) -> log(a b) and n log(a) -> log(a^n) for numeric arguments.</summary>
    private static Expr CombineLogs(Expr e)
    {
        if (e is not AddExpr a) return e;
        var logs = new List<Expr>();
        var others = new List<Expr>();
        foreach (var t in a.Terms)
        {
            var (c, rest) = t.SplitCoefficient();
            if (rest is FnExpr { Name: "log" } lg && c is Num { Value.IsInteger: true } && lg.Args[0].IsConstant)
                logs.Add(Expr.Power(lg.Args[0], c));
            else others.Add(t);
        }
        if (logs.Count < 2) return e;
        return Expr.Plus(others.Append(Expr.Call("log", Expr.Times(logs))));
    }
}
