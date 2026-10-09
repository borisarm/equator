namespace Equator.Core.Calculus;

public static class Derivative
{
    public static Expr Diff(Expr e, Expr x, int order)
    {
        for (int i = 0; i < order; i++) e = Diff(e, x);
        return e;
    }

    public static Expr Diff(Expr e, Expr x)
    {
        if (x is not Sym) throw new MathException(Messages.T("Can only differentiate with respect to a symbol."));
        if (e.FreeOf(x)) return e is ListExpr l ? Expr.List(l.Items.Select(_ => (Expr)Expr.Zero)) : Expr.Zero;
        switch (e)
        {
            case Sym:
                return e == x ? Expr.One : Expr.Zero;
            case AddExpr a:
                return Expr.Plus(a.Terms.Select(t => Diff(t, x)));
            case MulExpr m:
                {
                    // Product rule: sum over i of f_i' * prod_{j != i} f_j
                    var terms = new List<Expr>();
                    for (int i = 0; i < m.Factors.Count; i++)
                    {
                        if (m.Factors[i].FreeOf(x)) continue;
                        var d = Diff(m.Factors[i], x);
                        terms.Add(Expr.Times(m.Factors.Select((f, j) => j == i ? d : f)));
                    }
                    return Expr.Plus(terms);
                }
            case PowExpr p:
                {
                    var b = p.Base; var n = p.Exponent;
                    if (n.FreeOf(x)) return n * Expr.Power(b, n - 1) * Diff(b, x);
                    if (b.FreeOf(x)) return e * Expr.Call("log", b) * Diff(n, x);
                    // General: d(b^n) = b^n (n' log b + n b'/b)
                    return e * (Diff(n, x) * Expr.Call("log", b) + n * Diff(b, x) / b);
                }
            case FnExpr f:
                return DiffFunction(f, x);
            case ListExpr l:
                return Expr.List(l.Items.Select(i => Diff(i, x)));
            case EqExpr q:
                return Expr.Equation(Diff(q.Lhs, x), Diff(q.Rhs, x));
        }
        throw new MathException(Messages.T("Cannot differentiate {0}.", e));
    }

    private static Expr DiffFunction(FnExpr f, Expr x)
    {
        if (f.Name == "diff" && f.Args.Count >= 2)
        {
            // Unevaluated derivative of an unknown function: raise the order.
            if (f.Args[1] == x)
            {
                int order = f.Args.Count == 3 && f.Args[2] is Num n ? (int)n.Value.Num : 1;
                return FnExpr.Inert("diff", f.Args[0], x, order + 1);
            }
            return FnExpr.Inert("diff", f, x);
        }
        if (f.Name == "integrate" && f.Args.Count == 2 && f.Args[1] == x) return f.Args[0];
        if (f.Name == "integrate" && f.Args.Count == 4 && f.Args[1] != x)
        {
            // Leibniz rule for variable limits.
            var (body, t, a, b) = (f.Args[0], f.Args[1], f.Args[2], f.Args[3]);
            var r = body.Substitute(t, b) * Diff(b, x) - body.Substitute(t, a) * Diff(a, x);
            if (!body.FreeOf(x)) r += FnExpr.Inert("integrate", Diff(body, x), t, a, b);
            return r;
        }

        if (f.Args.Count != 1)
        {
            // Unknown multi-argument function: chain rule needs partial derivatives; leave unevaluated.
            return FnExpr.Inert("diff", f, x);
        }

        var u = f.Args[0];
        var du = Diff(u, x);
        Expr? outer = f.Name switch
        {
            "sin" => Expr.Call("cos", u),
            "cos" => -Expr.Call("sin", u),
            "tan" => Expr.Power(Expr.Call("sec", u), 2),
            "cot" => -Expr.Power(Expr.Call("csc", u), 2),
            "sec" => Expr.Call("sec", u) * Expr.Call("tan", u),
            "csc" => -Expr.Call("csc", u) * Expr.Call("cot", u),
            "asin" => Expr.One / Expr.Sqrt(1 - Expr.Power(u, 2)),
            "acos" => -Expr.One / Expr.Sqrt(1 - Expr.Power(u, 2)),
            "atan" => Expr.One / (1 + Expr.Power(u, 2)),
            "acot" => -Expr.One / (1 + Expr.Power(u, 2)),
            "asec" => Expr.One / (Expr.Call("abs", u) * Expr.Sqrt(Expr.Power(u, 2) - 1)),
            "acsc" => -Expr.One / (Expr.Call("abs", u) * Expr.Sqrt(Expr.Power(u, 2) - 1)),
            "sinh" => Expr.Call("cosh", u),
            "cosh" => Expr.Call("sinh", u),
            "tanh" => Expr.Power(Expr.Call("sech", u), 2),
            "coth" => -Expr.Power(Expr.Call("csch", u), 2),
            "sech" => -Expr.Call("sech", u) * Expr.Call("tanh", u),
            "csch" => -Expr.Call("csch", u) * Expr.Call("coth", u),
            "asinh" => Expr.One / Expr.Sqrt(Expr.Power(u, 2) + 1),
            "acosh" => Expr.One / (Expr.Sqrt(u - 1) * Expr.Sqrt(u + 1)),
            "atanh" => Expr.One / (1 - Expr.Power(u, 2)),
            "log" => Expr.One / u,
            "abs" => Expr.Call("sign", u),
            "sign" => Expr.Zero,
            "erf" => 2 / Expr.Sqrt(Expr.Pi) * Expr.Exp(-Expr.Power(u, 2)),
            "floor" or "ceil" or "round" => Expr.Zero,
            _ => null,
        };
        if (outer is null)
        {
            // Unknown function f(u): f'(u) * u'
            var inner = FnExpr.Inert("diff", f, x);
            if (u == x) return inner;
            return FnExpr.Inert("diff", FnExpr.Inert(f.Name, u), u) * du;
        }
        return outer * du;
    }
}
