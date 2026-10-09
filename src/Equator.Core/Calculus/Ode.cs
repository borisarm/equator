using Equator.Core.Algebra;

namespace Equator.Core.Calculus;

/// <summary>
/// Ordinary differential equations: first-order separable and linear equations, and linear equations with
/// constant coefficients of any order (characteristic roots plus variation of parameters).
/// Derivatives are written y', y'' (parsed to the inert node @d(y, n)).
/// </summary>
public static class Ode
{
    public static Expr Solve(Expr input, Sym y, Sym x)
    {
        var items = input is ListExpr l ? l.Items.ToList() : [input];
        var odes = new List<Expr>();
        var conditions = new List<(int Order, Expr Point, Expr Value)>();
        foreach (var item in items)
        {
            if (item is not EqExpr eq) { odes.Add(item); continue; }
            var cond = AsCondition(eq, y, x);
            if (cond is not null) conditions.Add(cond.Value);
            else odes.Add(eq.Lhs - eq.Rhs);
        }
        if (odes.Count != 1) throw new MathException(Messages.T("dsolve: give exactly one differential equation (plus optional conditions like y(0) = 1)."));

        // Normalise to an expression G(x, y, y1, ..., yn) = 0 with plain symbols.
        var ode = Normalise(odes[0], y, x);
        int order = OrderOf(ode, y);
        if (order == 0) throw new MathException(Messages.T("dsolve: the equation contains no derivative of {0}.", y));
        var ys = Enumerable.Range(0, order + 1).Select(k => k == 0 ? (Expr)y : new Sym($"\u0001{y.Name}{k}")).ToList();
        var G = ode.MapBottomUp(e => e is FnExpr { Name: "@d" } d && d.Args[0] == y ? ys[(int)((Num)d.Args[1]).Value.Num] : e);

        var constants = Enumerable.Range(1, order).Select(i => (Expr)new Sym("C" + i)).ToList();
        List<Expr> solutions;
        if (TryConstantCoefficients(G, ys, x, constants) is { } linear) solutions = [linear];
        else if (order == 1) solutions = FirstOrder(G, y, ys[1], x, constants[0]);
        else throw new MathException(Messages.T("dsolve: only linear equations with constant coefficients are supported for order ≥ 2."));

        if (conditions.Count > 0) solutions = solutions.Select(s => ApplyConditions(s, y, x, conditions, constants)).ToList();
        var lhs = FnExpr.Inert(y.Name, x);
        var result = solutions.Select(s => Expr.Equation(lhs, s)).ToList();
        return result.Count == 1 ? result[0] : Expr.List(result);
    }

    private static (int, Expr, Expr)? AsCondition(EqExpr eq, Sym y, Sym x)
    {
        // y(a) = v
        if (eq.Lhs is FnExpr f && f.Name == y.Name && f.Args.Count == 1 && f.Args[0].FreeOf(x)) return (0, f.Args[0], eq.Rhs);
        // y'(a) = v arrives as diff(y(a), a[, n])
        if (eq.Lhs is FnExpr { Name: "diff" } d && d.Args[0] is FnExpr inner && inner.Name == y.Name && inner.Args.Count == 1 && inner.Args[0].FreeOf(x))
        {
            int n = d.Args.Count == 3 ? (int)((Num)d.Args[2]).Value.Num : 1;
            return (n, inner.Args[0], eq.Rhs);
        }
        return null;
    }

    private static Expr Normalise(Expr e, Sym y, Sym x) => e.MapBottomUp(z => z switch
    {
        FnExpr f when f.Name == y.Name && f.Args.Count == 1 && f.Args[0] == x => y,
        FnExpr { Name: "diff" } d when d.Args[0] == y && d.Args[1] == x => FnExpr.Inert("@d", y, d.Args.Count == 3 ? d.Args[2] : Expr.One),
        _ => z,
    });

    private static int OrderOf(Expr e, Sym y) =>
        e.Subexpressions().OfType<FnExpr>().Where(f => f.Name == "@d" && f.Args[0] == y).Select(f => (int)((Num)f.Args[1]).Value.Num).DefaultIfEmpty(0).Max();

    // ---- constant coefficients ------------------------------------------------------------------------

    private static Expr? TryConstantCoefficients(Expr G, List<Expr> ys, Sym x, List<Expr> constants)
    {
        int n = ys.Count - 1;
        var ex = Expansion.Expand(G);
        var coeffs = new Expr[n + 1];
        var rest = ex;
        for (int k = 0; k <= n; k++)
        {
            var c = PolyView.Coefficients(ex, ys[k]);
            if (c is null || c.Count > 2) return null;
            coeffs[k] = c.Count == 2 ? c[1] : Expr.Zero;
            if (!coeffs[k].FreeOf(x) || ys.Any(v => !coeffs[k].FreeOf(v))) return null;
            rest = rest.Substitute(ys[k], Expr.Zero);
        }
        var forcing = -rest; // Σ c_k y^(k) = forcing(x)
        if (ys.Any(v => !forcing.FreeOf(v))) return null;

        var lambda = new Sym("\u0001lambda");
        var charPoly = Expr.Plus(coeffs.Select((c, k) => c * Expr.Power(lambda, k)));
        var roots = Solver.PolynomialRoots(charPoly, lambda, withMultiplicity: true);
        if (roots is null || roots.Count != n) return null;

        var basis = new List<Expr>();
        foreach (var group in roots.GroupBy(r => r))
        {
            var r = group.Key;
            int mult = group.Count();
            var (re, im) = Split(r);
            if (im.IsZero)
            {
                for (int j = 0; j < mult; j++) basis.Add(Expr.Power(x, j) * Expr.Exp(r * x));
            }
            else if (!im.IsNegativeNumber && !(im.HasNegativeCoefficient))
            {
                for (int j = 0; j < mult; j++)
                {
                    var e = Expr.Power(x, j) * Expr.Exp(re * x);
                    basis.Add(e * Expr.Call("cos", im * x));
                    basis.Add(e * Expr.Call("sin", im * x));
                }
            }
        }
        if (basis.Count != n) return null;

        var homogeneous = Expr.Plus(basis.Select((b, i) => constants[i] * b));
        if (forcing.IsZero) return homogeneous;

        var particular = VariationOfParameters(basis, forcing / coeffs[n], x);
        if (particular is null) throw new MathException(Messages.T("dsolve: could not find a particular solution (the required integrals were not found)."));
        return homogeneous + Simplifier.Simplify(particular);
    }

    private static (Expr Re, Expr Im) Split(Expr r)
    {
        var e = Expansion.Expand(r);
        var c = PolyView.Coefficients(e, Expr.I);
        if (c is null) return (r, Expr.Zero);
        Expr re = Expr.Zero, im = Expr.Zero;
        for (int k = 0; k < c.Count; k++)
        {
            switch (k % 4)
            {
                case 0: re += c[k]; break;
                case 1: im += c[k]; break;
                case 2: re -= c[k]; break;
                case 3: im -= c[k]; break;
            }
        }
        return (re, im);
    }

    private static Expr? VariationOfParameters(List<Expr> basis, Expr g, Sym x)
    {
        int n = basis.Count;
        var W = new Expr[n, n];
        for (int j = 0; j < n; j++)
        {
            var d = basis[j];
            for (int i = 0; i < n; i++)
            {
                W[i, j] = d;
                d = Derivative.Diff(d, x);
            }
        }
        var rhs = Enumerable.Range(0, n).Select(i => i == n - 1 ? g : Expr.Zero).ToList();
        var uPrime = (ListExpr)LinearAlgebra.LinSolve(LinearAlgebra.FromArray(W), Expr.List(rhs));
        Expr yp = Expr.Zero;
        for (int j = 0; j < n; j++)
        {
            var integrand = Simplifier.Simplify(uPrime.Items[j]);
            var u = Integrator.Antiderivative(integrand, x) ?? Integrator.Antiderivative(Expansion.Expand(integrand), x);
            if (u is null) return null;
            yp += u * basis[j];
        }
        return Expansion.Expand(yp);
    }

    // ---- first order ------------------------------------------------------------------------------------

    private static List<Expr> FirstOrder(Expr G, Sym y, Expr y1, Sym x, Expr C)
    {
        var slopes = Solver.SolveFor(G, y1, out _);
        if (slopes.Count == 0) throw new MathException(Messages.T("dsolve: could not solve the equation for {0}'.", y.Name));
        var g = slopes[0]; // y' = g(x, y)

        // Linear: y' = a1(x) y + a0(x)
        var c = PolyView.Coefficients(g, y);
        if (c is not null && c.Count <= 2 && c.All(k => k.FreeOf(y)))
        {
            var a0 = c[0];
            var a1 = c.Count == 2 ? c[1] : Expr.Zero;
            var intA1 = Integrator.Antiderivative(-a1, x);
            if (intA1 is not null)
            {
                var mu = Simplifier.Simplify(Expr.Exp(intA1));
                var integral = a0.IsZero ? Expr.Zero : Integrator.Antiderivative(Simplifier.Simplify(mu * a0), x);
                if (integral is not null)
                    return [Simplifier.Simplify(Expansion.Expand((integral + C) / mu))];
            }
        }

        // Separable: y' = A(x) B(y)
        var factored = Factoring.Factor(g);
        Expr A = Expr.One, B = Expr.One;
        foreach (var f in factored is MulExpr m ? m.Factors : [factored])
        {
            if (f.FreeOf(y)) A *= f;
            else if (f.FreeOf(x)) B *= f;
            else throw new MathException(Messages.T("dsolve: this first-order equation is neither linear nor separable."));
        }
        var H = Integrator.Antiderivative(Expr.One / B, y);
        var K = Integrator.Antiderivative(A, x);
        if (H is null || K is null) throw new MathException(Messages.T("dsolve: could not integrate the separated equation."));
        var implicitEq = H - K - C;
        try
        {
            var explicitSols = Solver.SolveFor(implicitEq, y, out var numeric);
            if (!numeric && explicitSols.Count > 0) return explicitSols.Select(Simplifier.Simplify).ToList();
        }
        catch (MathException) { }
        throw new MathException(Messages.T("dsolve: implicit solution {0} = {1} + {2}.", H, K, C));
    }

    // ---- initial conditions --------------------------------------------------------------------------

    private static Expr ApplyConditions(Expr sol, Sym y, Sym x, List<(int Order, Expr Point, Expr Value)> conds, List<Expr> constants)
    {
        var eqs = new List<Expr>();
        foreach (var (order, point, value) in conds)
        {
            var d = Derivative.Diff(sol, x, order);
            eqs.Add(Expr.Equation(Simplifier.Simplify(d.Substitute(x, point)), value));
        }
        var used = constants.Where(c => !sol.FreeOf(c)).ToList();
        var result = Solver.Solve(Expr.List(eqs), Expr.List(used));
        if (result is not ListExpr { Items.Count: > 0 } sols) throw new MathException(Messages.T("dsolve: the initial conditions are inconsistent."));
        var first = sols.Items[0];
        var rules = first is ListExpr fl ? fl.Items : [first];
        var map = rules.Cast<EqExpr>().ToDictionary(r => r.Lhs, r => r.Rhs);
        return Simplifier.Simplify(sol.Substitute(map));
    }
}
