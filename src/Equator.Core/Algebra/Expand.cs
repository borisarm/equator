namespace Equator.Core.Algebra;

public static class Expansion
{
    private const int MaxPower = 400;

    /// <summary>Distributes products over sums and expands positive integer powers of sums, recursively.</summary>
    public static Expr Expand(Expr e) => Expand(e, deep: true);

    /// <summary>Like <see cref="Expand(Expr)"/> but leaves function arguments untouched.</summary>
    public static Expr ExpandShallow(Expr e) => Expand(e, deep: false);

    private static Expr Expand(Expr e, bool deep)
    {
        if (!deep && e is FnExpr) return e;
        switch (e)
        {
            case AddExpr a:
                return Expr.Plus(a.Terms.Select(t => Expand(t, deep)));
            case MulExpr m:
                {
                    var acc = new List<Expr> { Expr.One };
                    foreach (var f in m.Factors)
                        acc = Distribute(acc, Expand(f, deep));
                    return Expr.Plus(acc);
                }
            case PowExpr p:
                {
                    var b = Expand(p.Base, deep);
                    var ex = Expand(p.Exponent, deep);
                    if (b is AddExpr && ex is Num { Value.IsInteger: true } n)
                    {
                        var k = n.Value.Num;
                        if (k > 0 && k <= MaxPower) return ExpandPower(b, (int)k);
                        if (k < 0 && k >= -MaxPower) return Expr.Power(ExpandPower(b, (int)-k), Expr.MinusOne);
                    }
                    // (a*b)^n where the product contains sums: expand it as a product.
                    var r = Expr.Power(b, ex);
                    return r is MulExpr ? Expand(r, deep) : r;
                }
            case FnExpr f:
                return f.WithArgs(f.Args.Select(a => Expand(a, deep)).ToArray());
            case ListExpr or EqExpr:
                return e.WithArgs(e.Args.Select(a => Expand(a, deep)).ToArray());
        }
        return e;
    }

    private static List<Expr> TermsOf(Expr e) => e is AddExpr a ? [.. a.Terms] : [e];

    private static List<Expr> Distribute(List<Expr> acc, Expr factor)
    {
        Engine.Interrupt.Check();
        var terms = TermsOf(factor);
        if (terms.Count == 1 && acc.Count == 1) return [Expr.Times(acc[0], terms[0])];
        var products = new List<Expr>(acc.Count * terms.Count);
        foreach (var a in acc)
            foreach (var t in terms)
                products.Add(Expr.Times(a, t));
        // Combine like terms after each step to keep intermediate sizes small.
        return TermsOf(Expr.Plus(products)).SelectMany(t => t is MulExpr ? TermsOf(DistributeOne(t)) : [t]).ToList();
    }

    /// <summary>A product produced while distributing may still hide a sum factor (e.g. 2*(x+1)); expand it.</summary>
    private static Expr DistributeOne(Expr t)
    {
        if (t is MulExpr m && m.Factors.Any(f => f is AddExpr))
        {
            var acc = new List<Expr> { Expr.One };
            foreach (var f in m.Factors)
            {
                var terms = TermsOf(f);
                var next = new List<Expr>();
                foreach (var a in acc)
                    foreach (var x in terms)
                        next.Add(Expr.Times(a, x));
                acc = next;
            }
            return Expr.Plus(acc);
        }
        return t;
    }

    private static Expr ExpandPower(Expr b, int n)
    {
        // Binary exponentiation with distribution at each step.
        Expr result = Expr.One;
        Expr square = b;
        while (n > 0)
        {
            if ((n & 1) == 1) result = Expr.Plus(Distribute(TermsOf(result), square));
            n >>= 1;
            if (n > 0) square = Expr.Plus(Distribute(TermsOf(square), square));
        }
        return result;
    }

    /// <summary>Expands only the numerator, keeping denominators as they are (used before polynomial extraction).</summary>
    public static Expr ExpandNumerator(Expr e)
    {
        var (n, d) = Rational.NumDen(e);
        return Expand(n) / d;
    }
}
