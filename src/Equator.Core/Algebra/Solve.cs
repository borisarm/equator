using Equator.Core.Numerics;

namespace Equator.Core.Algebra;

/// <summary>Equation solving: polynomial roots, isolation by inverse functions, substitution, systems and numeric fallback.</summary>
public static class Solver
{
    /// <summary>Roots of a polynomial in x, exact where possible, numeric otherwise. Null if p is not polynomial in x.</summary>
    public static List<Expr>? PolynomialRoots(Expr p, Expr x, bool withMultiplicity = false)
    {
        var coeffs = PolyView.Coefficients(p, x);
        if (coeffs is null) return null;
        int n = coeffs.Count - 1;
        var roots = new List<Expr>();
        if (n < 1) return roots;

        if (coeffs.All(c => c is Num))
        {
            var q = new QPoly(coeffs.Select(c => ((Num)c).Value));
            var (_, factors) = q.Factor();
            foreach (var (f, mult) in factors)
            {
                var fr = FactorRoots(f);
                for (int k = 0; k < (withMultiplicity ? mult : 1); k++) roots.AddRange(fr);
            }
            return roots;
        }

        if (coeffs.All(c => c.IsNumber || (c.IsConstant && !c.IsUndefinedOrInfinite && c.ContainsAny(z => z is Real))))
        {
            var dc = coeffs.Select(NumericEval.EvalReal).ToArray();
            if (dc.All(double.IsFinite))
                return NumericMethods.PolynomialRoots(dc).Select(NumericEval.FromComplex).ToList();
        }

        // Pull out x^k when the low coefficients vanish.
        int zeros = 0;
        while (zeros < n && coeffs[zeros].IsZero) zeros++;
        if (zeros > 0)
        {
            for (int k = 0; k < (withMultiplicity ? zeros : 1); k++) roots.Add(Expr.Zero);
            coeffs = coeffs.Skip(zeros).ToList();
            n = coeffs.Count - 1;
            if (n == 0) return roots;
        }

        switch (n)
        {
            case 1:
                roots.Add(Tidy(-coeffs[0] / coeffs[1]));
                return roots;
            case 2:
                roots.AddRange(Quadratic(coeffs[2], coeffs[1], coeffs[0]));
                return roots;
        }

        // Biquadratic-like: only powers that are multiples of k.
        for (int k = n / 2; k >= 2; k--)
        {
            if (n % k != 0) continue;
            if (!coeffs.Select((c, i) => (c, i)).All(t => t.i % k == 0 || t.c.IsZero)) continue;
            var y = new Sym("\u0001y");
            var reduced = Expr.Plus(coeffs.Select((c, i) => i % k == 0 ? c * Expr.Power(y, i / k) : Expr.Zero));
            var yr = PolynomialRoots(reduced, y, withMultiplicity);
            if (yr is null) break;
            foreach (var r in yr)
                roots.AddRange(KthRoots(r, k));
            return roots;
        }
        return null;
    }

    private static IEnumerable<Expr> KthRoots(Expr r, int k)
    {
        var principal = Expr.Power(r, new Num(new BigRational(1, k)));
        if (k == 2) return [Tidy(-principal), Tidy(principal)];
        var list = new List<Expr>();
        for (int j = 0; j < k; j++)
            list.Add(Tidy(principal * Expr.Exp(2 * Expr.Pi * Expr.I * new Num(new BigRational(j, k)))));
        return list;
    }

    private static List<Expr> FactorRoots(QPoly f)
    {
        switch (f.Degree)
        {
            case 1:
                return [new Num(-f[0] / f[1])];
            case 2:
                return Quadratic(new Num(f[2]), new Num(f[1]), new Num(f[0]));
        }
        // Even polynomial: substitute y = x^2.
        if (f.Coeffs.Select((c, i) => (c, i)).All(t => t.i % 2 == 0 || t.c.IsZero) && f.Degree == 4)
        {
            var y = new Sym("\u0001y");
            var reduced = Expr.Plus(f.Coeffs.Select((c, i) => i % 2 == 0 ? new Num(c) * Expr.Power(y, i / 2) : Expr.Zero));
            var yr = PolynomialRoots(reduced, y);
            if (yr is not null && yr.All(r => !r.ContainsAny(z => z is Real)))
                return yr.SelectMany(r => KthRoots(r, 2)).ToList();
        }
        // Irreducible cubic or higher: numeric roots.
        return f.NumericRoots().Select(NumericEval.FromComplex).ToList();
    }

    private static List<Expr> Quadratic(Expr a, Expr b, Expr c)
    {
        var disc = Tidy(Expansion.Expand(b * b - 4 * a * c));
        if (disc.IsZero) return [Tidy(-b / (2 * a))];
        var s = Expr.Sqrt(disc);
        var r1 = Tidy(Tidy(-b - s) / (2 * a));
        var r2 = Tidy(Tidy(-b + s) / (2 * a));
        return [r1, r2];
    }

    /// <summary>Picks the simplest of a few equivalent forms of a solution value.</summary>
    public static Expr Tidy(Expr e)
    {
        if (e.IsNumber || e is Sym) return e;
        var best = e;
        int bestLen = Printing.TextFormatter.Format(e).Length;
        foreach (var cand in new[] { Expansion.Expand(e), Rational.Cancel(e), Factoring.Factor(e) })
        {
            int len = Printing.TextFormatter.Format(cand).Length;
            if (len < bestLen || (len == bestLen && cand.Complexity() < best.Complexity() - 1)) { best = cand; bestLen = len; }
        }
        return best;
    }

    // ---- single equation ----------------------------------------------------------------------------

    /// <summary>Solves a single equation (or expression = 0) for x. Results are values of x.</summary>
    public static List<Expr> SolveFor(Expr equation, Expr x, out bool numeric)
    {
        numeric = false;
        var f = equation is EqExpr eq ? eq.Lhs - eq.Rhs : equation;
        if (f.FreeOf(x))
        {
            if (Simplifier.Simplify(f).IsZero) throw new MathException($"The equation holds for every value of {x}.");
            return [];
        }

        var (num, den) = Rational.NumDen(Rational.Together(f));
        var candidates = new List<Expr>();
        bool solvedAll = true;
        foreach (var factor in FactorsOf(num))
        {
            if (factor.FreeOf(x)) continue;
            var sols = SolveFactor(factor, x);
            if (sols is null) { solvedAll = false; break; }
            candidates.AddRange(sols);
        }

        if (!solvedAll)
        {
            // A scan finds roots but cannot prove there are no others, so this stays flagged as numeric
            // even when every root snaps to an exact value.
            candidates = NumericSolve(f, x).Select(s => SnapToExact(f, x, s)).ToList();
            numeric = true;
        }

        // Discard spurious solutions: zeros of the denominator, or values that fail a numeric check.
        var result = new List<Expr>();
        foreach (var s in candidates)
        {
            if (result.Contains(s)) continue;
            if (!den.IsOne && den.FreeOf(x) == false)
            {
                var dv = Simplifier.Simplify(den.Substitute(x, s));
                if (dv.IsZero) continue;
            }
            if (!numeric && !Verify(f, x, s)) continue;
            result.Add(s);
        }
        return SortSolutions(result);
    }

    /// <summary>Orders numeric solutions by real part, then imaginary part; symbolic ones keep their order.</summary>
    private static List<Expr> SortSolutions(List<Expr> sols)
    {
        var keyed = sols.Select((s, i) =>
        {
            System.Numerics.Complex v = double.NaN;
            if (s.IsConstant && !s.IsUndefinedOrInfinite)
            {
                try { v = NumericEval.Eval(s); } catch (MathException) { }
            }
            return (s, i, v);
        }).ToList();
        if (keyed.Any(k => double.IsNaN(k.v.Real))) return sols;
        return keyed.OrderBy(k => Math.Abs(k.v.Imaginary) > 1e-12 ? 1 : 0).ThenBy(k => k.v.Real).ThenBy(k => k.v.Imaginary).Select(k => k.s).ToList();
    }

    private static IEnumerable<Expr> FactorsOf(Expr num)
    {
        Expr factored;
        try { factored = Factoring.Factor(num); }
        catch (MathException) { factored = num; }
        foreach (var f in factored is MulExpr m ? m.Factors : [factored])
        {
            if (f is PowExpr { Exponent: Num { Value.Sign: > 0 } } p) yield return p.Base;
            else yield return f;
        }
    }

    private static bool Verify(Expr f, Expr x, Expr s)
    {
        // Only checkable when everything else is numeric.
        var v = f.Substitute(x, s);
        if (!v.IsConstant) return true;
        try
        {
            var c = NumericEval.Eval(v);
            if (double.IsNaN(c.Real)) return true;
            var scale = 1 + System.Numerics.Complex.Abs(NumericEval.Eval(s));
            return System.Numerics.Complex.Abs(c) < 1e-6 * scale;
        }
        catch (MathException) { return true; }
    }

    private static List<Expr>? SolveFactor(Expr g, Expr x)
    {
        var poly = PolynomialRoots(g, x);
        if (poly is not null) return poly;

        var iso = Isolate(g, Expr.Zero, x, 0);
        if (iso is not null) return iso;

        var sub = SolveBySubstitution(g, x);
        if (sub is not null) return sub;

        var rad = SolveRadical(g, x);
        if (rad is not null) return rad;

        var trig = SolveTrigRational(g, x);
        if (trig is not null) return trig;
        return null;
    }

    /// <summary>
    /// Equations with a radical u^(1/q): write g as a polynomial G(t) in t = u^(1/q) and eliminate t with the
    /// resultant of G(t) and t^q − u. Candidates may be spurious (squaring), so SolveFor verifies them.
    /// </summary>
    private static List<Expr>? SolveRadical(Expr g, Expr x)
    {
        var radical = g.Subexpressions()
            .OfType<PowExpr>()
            .Where(p => p.Exponent is Num { Value.IsInteger: false } && !p.Base.FreeOf(x))
            .OrderByDescending(p => p.Base.Complexity())
            .FirstOrDefault();
        if (radical is null) return null;
        var u = radical.Base;
        var q = (int)((Num)radical.Exponent).Value.Den;
        if (q > 12) return null;
        var t = new Sym("\u0001r");
        bool ok = true;
        var G = g.MapBottomUp(z =>
        {
            if (z is PowExpr pz && pz.Base == u && pz.Exponent is Num { Value: var e })
            {
                var k = e * q;
                if (k.IsInteger) return Expr.Power(t, new Num(k));
                ok = false;
            }
            return z;
        });
        if (!ok) return null;
        var (Gn, _) = Rational.NumDen(Rational.Together(G));
        var coeffs = PolyView.Coefficients(Gn, t);
        if (coeffs is null || coeffs.Count < 2 || coeffs.Count > 7) return null;
        // t^q − u as a coefficient list.
        var root = Enumerable.Repeat((Expr)Expr.Zero, q + 1).ToList();
        root[0] = -u; root[q] = Expr.One;
        var res = Expansion.Expand(Resultant(coeffs, root));
        if (res.IsZero || res.FreeOf(x) || res.Contains(t)) return null;
        try
        {
            var sols = SolveFor(res, x, out var numeric);
            return numeric ? null : sols;
        }
        catch (MathException) { return null; }
    }

    /// <summary>Resultant of two polynomials given by coefficient lists (constant term first), via the Sylvester matrix.</summary>
    public static Expr Resultant(IReadOnlyList<Expr> a, IReadOnlyList<Expr> b)
    {
        int m = a.Count - 1, n = b.Count - 1;
        int size = m + n;
        var s = new Expr[size, size];
        for (int i = 0; i < size; i++) for (int j = 0; j < size; j++) s[i, j] = Expr.Zero;
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= m; j++) s[i, i + j] = a[m - j];
        for (int i = 0; i < m; i++)
            for (int j = 0; j <= n; j++) s[n + i, i + j] = b[n - j];
        return LinearAlgebra.Determinant(LinearAlgebra.FromArray(s));
    }

    /// <summary>
    /// Equations rational in sin, cos, tan, ... of a single argument u: substitute T = tan(u/2) (Weierstrass),
    /// solve the polynomial in T and map back with u = 2 atan(T), plus u = π (T = ∞). Gives the solutions
    /// with u in (−π, π].
    /// </summary>
    private static List<Expr>? SolveTrigRational(Expr g, Expr x)
    {
        var trig = g.Subexpressions().OfType<FnExpr>()
            .Where(f => f.Name is "sin" or "cos" or "tan" or "cot" or "sec" or "csc" && !f.Args[0].FreeOf(x))
            .ToList();
        if (trig.Count == 0) return null;
        var u = trig[0].Args[0];
        if (trig.Any(f => f.Args[0] != u)) return null;
        var T = new Sym("\u0001w");
        var one = Expr.One;
        var T2 = Expr.Power(T, 2);
        var map = new Dictionary<Expr, Expr>();
        foreach (var f in trig.Distinct())
        {
            map[f] = f.Name switch
            {
                "sin" => 2 * T / (one + T2),
                "cos" => (one - T2) / (one + T2),
                "tan" => 2 * T / (one - T2),
                "cot" => (one - T2) / (2 * T),
                "sec" => (one + T2) / (one - T2),
                _ => (one + T2) / (2 * T),
            };
        }
        var h = g.Substitute(map);
        if (!h.FreeOf(x)) return null;
        var (hn, _) = Rational.NumDen(Rational.Together(h));
        var roots = PolynomialRoots(Expansion.Expand(hn), T);
        if (roots is null) return null;
        var values = new List<Expr>();
        var real = roots.Where(IsRealConstant).ToList();
        foreach (var r in real.Count > 0 ? real : roots)
            values.Add(2 * Expr.Call("atan", r));
        // T = ∞ corresponds to u = π.
        var atPi = Simplifier.Simplify(g.Substitute(u, Expr.Pi));
        if (atPi.IsZero) values.Add(Expr.Pi);
        var result = new List<Expr>();
        foreach (var v in values)
        {
            var sols = Isolate(u, Tidy(v), x, 0);
            if (sols is null) return null;
            foreach (var s in sols) if (!result.Contains(s)) result.Add(s);
        }
        return result;
    }

    private static bool IsRealConstant(Expr e)
    {
        if (!e.IsConstant || e.IsUndefinedOrInfinite) return false;
        try
        {
            var v = NumericEval.Eval(e);
            return Math.Abs(v.Imaginary) < 1e-12 * (1 + Math.Abs(v.Real));
        }
        catch (MathException) { return false; }
    }

    /// <summary>Replaces numeric roots that are exactly small rationals (checked symbolically) by the exact value.</summary>
    private static Expr SnapToExact(Expr f, Expr x, Expr s)
    {
        if (s is not Real r || !double.IsFinite(r.Value)) return s;
        var q = BigRational.FromDouble(r.Value, 1000);
        if (Math.Abs(q.ToDouble() - r.Value) > 1e-10 * (1 + Math.Abs(r.Value))) return s;
        try
        {
            var v = Simplifier.Simplify(f.Substitute(x, new Num(q)));
            return v.IsZero ? new Num(q) : s;
        }
        catch (MathException) { return s; }
    }

    /// <summary>Moves everything not containing x to the right-hand side, inverting one operation at a time.</summary>
    private static List<Expr>? Isolate(Expr lhs, Expr rhs, Expr x, int depth)
    {
        if (depth > 40) return null;
        Engine.Interrupt.Check();
        if (lhs == x) return [Tidy(rhs)];
        switch (lhs)
        {
            case AddExpr a:
                {
                    var with = a.Terms.Where(t => !t.FreeOf(x)).ToList();
                    if (with.Count != 1)
                    {
                        // Linear in x after expansion?
                        var c = PolyView.Coefficients(lhs - rhs, x);
                        if (c is { Count: 2 }) return [Tidy(-c[0] / c[1])];
                        return null;
                    }
                    var without = Expr.Plus(a.Terms.Where(t => t.FreeOf(x)));
                    return Isolate(with[0], rhs - without, x, depth + 1);
                }
            case MulExpr m:
                {
                    var with = m.Factors.Where(t => !t.FreeOf(x)).ToList();
                    if (with.Count != 1) return null;
                    var without = Expr.Times(m.Factors.Where(t => t.FreeOf(x)));
                    return Isolate(with[0], rhs / without, x, depth + 1);
                }
            case PowExpr p:
                {
                    if (p.Exponent.FreeOf(x))
                    {
                        var n = p.Exponent;
                        if (rhs.IsZero) return n.IsNegativeNumber ? [] : Isolate(p.Base, Expr.Zero, x, depth + 1);
                        if (n is Num { Value.IsInteger: true } k && k.Value.Num.IsEven && k.Value.Sign > 0)
                        {
                            var r = Expr.Power(rhs, Expr.One / n);
                            return Union(Isolate(p.Base, r, x, depth + 1), Isolate(p.Base, -r, x, depth + 1));
                        }
                        if (n is Num { Value.IsInteger: true } odd && !odd.Value.Num.IsEven && odd.Value.Sign > 0 && rhs.IsNegativeNumber)
                            return Isolate(p.Base, -Expr.Power(-rhs, Expr.One / n), x, depth + 1);
                        return Isolate(p.Base, Expr.Power(rhs, Expr.One / n), x, depth + 1);
                    }
                    if (p.Base.FreeOf(x))
                    {
                        if (rhs.IsZero) return [];
                        var logRhs = Expr.Call("log", rhs);
                        var target = p.Base == Expr.E ? logRhs : logRhs / Expr.Call("log", p.Base);
                        return Isolate(p.Exponent, target, x, depth + 1);
                    }
                    return null;
                }
            case FnExpr f when f.Args.Count == 1:
                {
                    var u = f.Args[0];
                    Expr[]? targets = f.Name switch
                    {
                        "sin" => [Expr.Call("asin", rhs), Expr.Pi - Expr.Call("asin", rhs)],
                        "cos" => [Expr.Call("acos", rhs), -Expr.Call("acos", rhs)],
                        "tan" => [Expr.Call("atan", rhs)],
                        "cot" => [Expr.Call("acot", rhs)],
                        "sec" => [Expr.Call("acos", Expr.One / rhs)],
                        "csc" => [Expr.Call("asin", Expr.One / rhs)],
                        "asin" => [Expr.Call("sin", rhs)],
                        "acos" => [Expr.Call("cos", rhs)],
                        "atan" => [Expr.Call("tan", rhs)],
                        "sinh" => [Expr.Call("asinh", rhs)],
                        "cosh" => [Expr.Call("acosh", rhs), -Expr.Call("acosh", rhs)],
                        "tanh" => [Expr.Call("atanh", rhs)],
                        "asinh" => [Expr.Call("sinh", rhs)],
                        "acosh" => [Expr.Call("cosh", rhs)],
                        "atanh" => [Expr.Call("tanh", rhs)],
                        "log" => [Expr.Exp(rhs)],
                        "abs" => [rhs, -rhs],
                        _ => null,
                    };
                    if (targets is null) return null;
                    List<Expr>? all = [];
                    foreach (var t in targets.Distinct())
                    {
                        all = Union(all, Isolate(u, t, x, depth + 1));
                        if (all is null) return null;
                    }
                    return all;
                }
        }
        return null;
    }

    private static List<Expr>? Union(List<Expr>? a, List<Expr>? b)
    {
        if (a is null || b is null) return null;
        var r = new List<Expr>(a);
        foreach (var e in b) if (!r.Contains(e)) r.Add(e);
        return r;
    }

    /// <summary>
    /// If g is a polynomial in a single kernel k(x) (e.g. e^x, sin(x), log(x)), solve for k then invert.
    /// Exponentials b^(n u) are rewritten as (b^u)^n first.
    /// </summary>
    private static List<Expr>? SolveBySubstitution(Expr g, Expr x)
    {
        g = Expansion.Expand(g);
        var t = new Sym("\u0001t");
        foreach (var kernel in KernelCandidates(g, x))
        {
            var replaced = ReplaceKernel(g, kernel, t);
            if (replaced is null || !replaced.FreeOf(x)) continue;
            var roots = PolynomialRoots(replaced, t);
            if (roots is null) continue;
            var result = new List<Expr>();
            foreach (var r in roots)
            {
                var sols = Isolate(kernel, r, x, 0);
                if (sols is null) { result = null; break; }
                foreach (var s in sols) if (!result.Contains(s)) result.Add(s);
            }
            if (result is not null) return result;
        }
        return null;
    }

    private static IEnumerable<Expr> KernelCandidates(Expr g, Expr x)
    {
        var seen = new HashSet<Expr>();
        foreach (var s in g.Subexpressions())
        {
            if (s.FreeOf(x) || s == x) continue;
            Expr? k = s switch
            {
                FnExpr => s,
                PowExpr { Base: var b, Exponent: var e } when b.FreeOf(x) => Expr.Power(b, e.SplitCoefficient().Term),
                PowExpr { Exponent: Num { Value.IsInteger: false } } => s,
                _ => null,
            };
            if (k is not null && seen.Add(k)) yield return k;
        }
    }

    private static Expr? ReplaceKernel(Expr g, Expr kernel, Sym t)
    {
        if (kernel is PowExpr { Base: var b, Exponent: var u } && b.FreeOf(t))
        {
            // b^(n u) -> t^n for integer n
            bool ok = true;
            var r = g.MapBottomUp(z =>
            {
                if (z is PowExpr pz && pz.Base == b)
                {
                    var (c, rest) = pz.Exponent.SplitCoefficient();
                    if (rest == u && c is Num { Value.IsInteger: true } n) return Expr.Power(t, n);
                    if (rest == u) ok = false;
                }
                return z;
            });
            return ok ? r : null;
        }
        return g.Substitute(kernel, t);
    }

    /// <summary>Numeric real roots of f(x) = 0 by scanning a range.</summary>
    public static List<Expr> NumericSolve(Expr f, Expr x, double a = -100, double b = 100)
    {
        if (!f.FreeSymbols().All(s => s == x))
            throw new MathException("Cannot solve this equation symbolically, and it contains other symbols so it cannot be solved numerically.");
        var fn = NumericEval.Compile1(f, ((Sym)x).Name);
        var roots = NumericMethods.RealRoots(fn, a, b, 20000);
        return roots.Select(r => (Expr)new Real(r)).ToList();
    }

    // ---- public entry points ------------------------------------------------------------------------

    /// <summary>
    /// solve(eqs, vars): a single variable yields a list of equations [x = a, x = b]; several variables
    /// yield a list of solutions, each a list of equations.
    /// </summary>
    public static Expr Solve(Expr equations, Expr? variables)
    {
        var eqs = equations is ListExpr l ? l.Items.ToList() : [equations];
        List<Expr> vars;
        if (variables is null)
        {
            vars = eqs.SelectMany(e => e.FreeSymbols()).Distinct().Cast<Expr>().ToList();
            if (vars.Count == 0) throw new MathException("No variable to solve for.");
            if (eqs.Count == 1 && vars.Count > 1)
                throw new MathException($"Specify the variable to solve for, e.g. solve(eq, {vars[0]}).");
        }
        else vars = variables is ListExpr vl ? vl.Items.ToList() : [variables];

        if (vars.Any(v => v is not Sym)) throw new MathException("Solve variables must be symbols.");

        if (eqs.Any(e => e is FnExpr { Name: "@rel" }))
        {
            if (vars.Count != 1) throw new MathException("solve: inequalities can only be solved for one variable.");
            if (eqs.Any(e => e is not FnExpr { Name: "@rel" }))
                throw new MathException("solve: cannot mix equations and inequalities.");
            return Inequalities.Solve(eqs.SelectMany(Inequalities.Conditions).ToList(), (Sym)vars[0]);
        }

        if (eqs.Count == 1 && vars.Count == 1)
        {
            var sols = SolveFor(eqs[0], vars[0], out _);
            return Expr.List(sols.Select(s => Expr.Equation(vars[0], s)));
        }

        var systemSols = SolveSystem(eqs.Select(e => e is EqExpr q ? q.Lhs - q.Rhs : e).ToList(), vars);
        return Expr.List(systemSols.Select(sol => Expr.List(vars.Where(sol.ContainsKey).Select(v => Expr.Equation(v, sol[v])))));
    }

    private static List<Dictionary<Expr, Expr>> SolveSystem(List<Expr> eqs, List<Expr> vars)
    {
        eqs = eqs.Select(e => Rational.NumDen(Rational.Together(e)).Num).Where(e => !e.IsZero).ToList();
        if (IsLinearSystem(eqs, vars)) return LinearSystem(eqs, vars);
        var result = Eliminate(eqs, vars, 0);
        if (result is not null) return result;
        return NumericSystem(eqs, vars);
    }

    private static bool IsLinearSystem(List<Expr> eqs, List<Expr> vars)
    {
        foreach (var e in eqs)
        {
            var ex = Expansion.Expand(e);
            foreach (var t in ex is AddExpr a ? a.Terms : [ex])
            {
                int deg = 0;
                foreach (var v in vars)
                {
                    var c = PolyView.Coefficients(t, v);
                    if (c is null) return false;
                    deg += c.Count - 1;
                }
                if (deg > 1) return false;
            }
        }
        return true;
    }

    private static List<Dictionary<Expr, Expr>> LinearSystem(List<Expr> eqs, List<Expr> vars)
    {
        int rows = eqs.Count, cols = vars.Count;
        var aug = new Expr[rows, cols + 1];
        for (int i = 0; i < rows; i++)
        {
            var e = Expansion.Expand(eqs[i]);
            var rest = e;
            for (int j = 0; j < cols; j++)
            {
                var c = PolyView.Coefficients(e, vars[j])!;
                aug[i, j] = c.Count > 1 ? c[1] : Expr.Zero;
            }
            var constant = e;
            foreach (var v in vars) constant = constant.Substitute(v, Expr.Zero);
            aug[i, cols] = -constant;
        }
        var (r, pivots) = LinearAlgebra.Rref(aug);
        if (pivots.Contains(cols)) return [];
        var sol = new Dictionary<Expr, Expr>();
        for (int i = 0; i < pivots.Count; i++)
        {
            var value = r[i, cols];
            for (int j = 0; j < cols; j++)
                if (!pivots.Contains(j) && !r[i, j].IsZero) value -= r[i, j] * vars[j];
            sol[vars[pivots[i]]] = Tidy(value);
        }
        return [sol];
    }

    private static List<Dictionary<Expr, Expr>>? Eliminate(List<Expr> eqs, List<Expr> vars, int depth)
    {
        if (depth > 8) return null;
        eqs = eqs.Where(e => !e.IsZero).ToList();
        if (vars.Count == 0 || eqs.Count == 0) return [new Dictionary<Expr, Expr>()];

        // Choose the equation/variable pair that is cheapest to solve.
        var options = new List<(int eqIndex, Expr v, int score)>();
        for (int i = 0; i < eqs.Count; i++)
            foreach (var v in vars)
            {
                if (eqs[i].FreeOf(v)) continue;
                int deg = PolyView.Degree(eqs[i], v);
                int score = (deg < 0 ? 50 : deg * 10) + eqs[i].FreeSymbols().Count(s => vars.Contains(s));
                options.Add((i, v, score));
            }
        if (options.Count == 0)
        {
            // Remaining equations don't involve the variables: consistent only if they vanish.
            return eqs.All(e => Simplifier.Simplify(e).IsZero) ? [new Dictionary<Expr, Expr>()] : [];
        }

        foreach (var (eqIndex, v, _) in options.OrderBy(o => o.score))
        {
            List<Expr> values;
            try { values = SolveFor(eqs[eqIndex], v, out var numeric); if (numeric) continue; }
            catch (MathException) { continue; }

            var all = new List<Dictionary<Expr, Expr>>();
            var otherVars = vars.Where(w => w != v).ToList();
            bool failed = false;
            foreach (var val in values)
            {
                var rest = eqs.Where((_, k) => k != eqIndex)
                    .Select(e => Rational.NumDen(Rational.Together(e.Substitute(v, val))).Num)
                    .Select(Expansion.Expand).ToList();
                var sub = Eliminate(rest, otherVars, depth + 1);
                if (sub is null) { failed = true; break; }
                foreach (var s in sub)
                {
                    var full = new Dictionary<Expr, Expr>(s);
                    full[v] = Tidy(val.Substitute(s));
                    if (!all.Any(d => d.Count == full.Count && d.All(kv => full.TryGetValue(kv.Key, out var o) && o == kv.Value)))
                        all.Add(full);
                }
            }
            if (!failed) return all;
        }
        return null;
    }

    private static List<Dictionary<Expr, Expr>> NumericSystem(List<Expr> eqs, List<Expr> vars)
    {
        if (eqs.Count != vars.Count || eqs.Any(e => e.FreeSymbols().Any(s => !vars.Contains(s))))
            throw new MathException("Could not solve this system.");
        var names = vars.Select(v => ((Sym)v).Name).ToArray();
        var fs = eqs.Select(e => NumericEval.Compile(e, names)).ToArray();
        var found = new List<double[]>();
        var rng = new Random(1);
        for (int attempt = 0; attempt < 60 && found.Count < 8; attempt++)
        {
            var x0 = vars.Select(_ => (rng.NextDouble() - 0.5) * (attempt < 20 ? 4 : 40)).ToArray();
            var sol = NumericMethods.NewtonSystem(fs, x0);
            if (sol is null) continue;
            if (found.Any(f => f.Zip(sol).All(p => Math.Abs(p.First - p.Second) < 1e-7 * Math.Max(1, Math.Abs(p.First))))) continue;
            found.Add(sol);
        }
        if (found.Count == 0) throw new MathException("Could not solve this system.");
        return found.Select(s => vars.Select((v, i) => (v, (Expr)new Real(Math.Abs(s[i]) < 1e-13 ? 0 : s[i]))).ToDictionary(p => p.v, p => p.Item2)).ToList();
    }
}
