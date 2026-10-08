using Equator.Core.Numerics;

namespace Equator.Core.Algebra;

/// <summary>
/// Real univariate inequalities f(x) op 0 by a sign chart: the real line is cut at the zeros of f, the poles
/// and the domain boundaries (sqrt, log, asin, ...), and each interval and cut point is tested.
/// </summary>
public static class Inequalities
{
    public static Expr Rel(string op, Expr lhs, Expr rhs) => FnExpr.Inert("@rel", new StrExpr(op), lhs, rhs);

    /// <summary>Splits a relation (possibly a chain a &lt; x &lt; b) into conditions f op 0.</summary>
    public static IEnumerable<(string Op, Expr F)> Conditions(Expr rel)
    {
        if (rel is not FnExpr { Name: "@rel" } r || r.Args[0] is not StrExpr op)
            throw new MathException("solve: expected an inequality.");
        var lhs = r.Args[1];
        if (lhs is FnExpr { Name: "@rel" } inner)
        {
            foreach (var c in Conditions(inner)) yield return c;
            lhs = inner.Args[2];
        }
        yield return (op.Value, lhs - r.Args[2]);
    }

    /// <summary>Solves a conjunction of conditions f op 0 for x; returns a list of conditions on x (empty when there is no solution).</summary>
    public static Expr Solve(IReadOnlyList<(string Op, Expr F)> conditions, Sym x)
    {
        foreach (var (_, f) in conditions)
        {
            if (f.FreeSymbols().Any(s => s != x))
                throw new MathException("solve: inequalities can only contain the variable being solved for.");
            if (f.ContainsAny(z => z is FnExpr { Name: "sin" or "cos" or "tan" or "cot" or "sec" or "csc" } t && !t.Args[0].FreeOf(x)))
                throw new MathException("solve: inequalities with periodic functions are not supported.");
        }

        var cuts = new List<(double Value, Expr Exact)>();
        void AddZeros(Expr g)
        {
            if (g.FreeOf(x)) return;
            List<Expr> sols;
            try { sols = Solver.SolveFor(g, x, out _); }
            catch (MathException) { return; }
            foreach (var s in sols)
            {
                if (!s.IsConstant || s.IsUndefinedOrInfinite) continue;
                System.Numerics.Complex v;
                try { v = NumericEval.Eval(s); }
                catch (MathException) { continue; }
                if (Math.Abs(v.Imaginary) > 1e-12 * (1 + Math.Abs(v.Real)) || !double.IsFinite(v.Real)) continue;
                if (!cuts.Any(c => Math.Abs(c.Value - v.Real) <= 1e-12 * (1 + Math.Abs(v.Real)))) cuts.Add((v.Real, s));
            }
        }

        foreach (var (_, f) in conditions)
        {
        var (num, den) = Rational.NumDen(Rational.Together(f));
        AddZeros(num);
        AddZeros(den);
        foreach (var s in f.Subexpressions())
        {
            switch (s)
            {
                case PowExpr { Exponent: Num { Value: var q } } p when !q.IsInteger && !p.Base.FreeOf(x): AddZeros(p.Base); break;
                case PowExpr { Exponent: var e } p when e.IsNegativeNumber && !p.Base.FreeOf(x): AddZeros(p.Base); break;
                case FnExpr { Name: "log" or "abs" or "sign" } fn when !fn.Args[0].FreeOf(x): AddZeros(fn.Args[0]); break;
                case FnExpr { Name: "asin" or "acos" or "atanh" } fn when !fn.Args[0].FreeOf(x):
                    AddZeros(fn.Args[0] - 1); AddZeros(fn.Args[0] + 1); break;
                case FnExpr { Name: "acosh" } fn when !fn.Args[0].FreeOf(x): AddZeros(fn.Args[0] - 1); break;
            }
        }
        }
        cuts.Sort((a, b) => a.Value.CompareTo(b.Value));

        static bool Holds(string op, double v) => op switch
        {
            "<" => v < 0,
            ">" => v > 0,
            "<=" => v <= 0,
            ">=" => v >= 0,
            "!=" => v != 0,
            _ => throw new MathException($"solve: unknown relation {op}."),
        };

        bool TestAt(double t) => conditions.All(c => TestAtOne(c.Op, c.F, t));
        bool TestAtOne(string op, Expr f, double t)
        {
            var vars = new Dictionary<string, System.Numerics.Complex> { [x.Name] = t };
            try
            {
                var v = NumericEval.Eval(f, vars);
                if (!double.IsFinite(v.Real) || Math.Abs(v.Imaginary) > 1e-9 * (1 + Math.Abs(v.Real))) return false;
                return Holds(op, v.Real);
            }
            catch (MathException) { return false; }
        }

        bool TestExact(Expr c) => conditions.All(cond => TestExactOne(cond.Op, cond.F, c));
        bool TestExactOne(string op, Expr f, Expr c)
        {
            Expr v;
            try { v = Simplifier.Simplify(f.Substitute(x, c)); }
            catch (MathException) { return false; }
            if (v.IsUndefinedOrInfinite) return false;
            if (v.IsZero) return Holds(op, 0);
            try
            {
                var n = NumericEval.Eval(v);
                if (!double.IsFinite(n.Real) || Math.Abs(n.Imaginary) > 1e-9 * (1 + Math.Abs(n.Real))) return false;
                return Holds(op, Math.Abs(n.Real) < 1e-13 ? 0 : n.Real);
            }
            catch (MathException) { return false; }
        }

        // Alternating sequence: interval 0, point 0, interval 1, point 1, ..., interval k.
        int k = cuts.Count;
        var intervalOk = new bool[k + 1];
        var pointOk = new bool[k];
        for (int i = 0; i <= k; i++)
        {
            double t;
            if (k == 0) t = 0.5;
            else if (i == 0) t = cuts[0].Value - 1 - Math.Abs(cuts[0].Value);
            else if (i == k) t = cuts[k - 1].Value + 1 + Math.Abs(cuts[k - 1].Value);
            else t = (cuts[i - 1].Value + cuts[i].Value) / 2;
            intervalOk[i] = TestAt(t);
        }
        for (int i = 0; i < k; i++) pointOk[i] = TestExact(cuts[i].Exact);

        var result = new List<Expr>();
        int pos = 0; // positions 0..2k: even = interval i/2, odd = point (pos-1)/2
        bool Ok(int p) => p % 2 == 0 ? intervalOk[p / 2] : pointOk[p / 2];
        while (pos <= 2 * k)
        {
            if (!Ok(pos)) { pos++; continue; }
            int start = pos;
            while (pos + 1 <= 2 * k && Ok(pos + 1)) pos++;
            int end = pos;
            result.Add(Segment(start, end, cuts, x));
            pos++;
        }
        return Expr.List(result);
    }

    /// <summary>The condition for positions start..end of the alternating interval/point sequence.</summary>
    private static Expr Segment(int start, int end, List<(double Value, Expr Exact)> cuts, Sym x)
    {
        if (start == end && start % 2 == 1) return Expr.Equation(x, cuts[start / 2].Exact);
        // Lower bound: an interval start is open at the previous cut; a point start is closed at that cut.
        Expr? lower = null; bool lowerClosed = false;
        if (start % 2 == 1) { lower = cuts[start / 2].Exact; lowerClosed = true; }
        else if (start / 2 > 0) lower = cuts[start / 2 - 1].Exact;
        Expr? upper = null; bool upperClosed = false;
        if (end % 2 == 1) { upper = cuts[end / 2].Exact; upperClosed = true; }
        else if (end / 2 < cuts.Count) upper = cuts[end / 2].Exact;

        if (lower is null && upper is null) return Rel("<", Rel("<", -Expr.Infinity, x), Expr.Infinity);
        if (lower is null) return Rel(upperClosed ? "<=" : "<", x, upper!);
        if (upper is null) return Rel(lowerClosed ? ">=" : ">", x, lower);
        return Rel(upperClosed ? "<=" : "<", Rel(lowerClosed ? "<=" : "<", lower, x), upper);
    }
}
