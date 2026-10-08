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

    private static IEnumerable<Expr> Candidates(Expr e)
    {
        yield return e;
        if (e.IsNumber || e is Sym) yield break;
        Engine.Interrupt.Check();

        var expanded = Safe(() => Expansion.Expand(e));
        if (expanded is not null) yield return expanded;

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
        }
    }

    private static Expr? Safe(Func<Expr> f)
    {
        try { return f(); }
        catch (MathException) { return null; }
        catch (DivideByZeroException) { return null; }
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
