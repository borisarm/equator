using System.Numerics;
using Equator.Core.Algebra;
using Equator.Core.Calculus;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Engine;

public sealed record Builtin(
    string Name,
    string Category,
    string Usage,
    string Description,
    string Example,
    int MinArgs,
    int MaxArgs,
    int[] BoundArgs,
    Func<Kernel, Expr[], Expr>? Handler,
    Func<Kernel, List<Expr>, Expr>? Lazy = null);

/// <summary>The library of built-in commands, with documentation used by help() and the UI palette.</summary>
public static class Builtins
{
    private static readonly Dictionary<string, Builtin> Table = new();
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["derivative"] = "diff", ["int"] = "integrate", ["integral"] = "integrate",
        ["lim"] = "limit", ["taylor"] = "series", ["evalf"] = "N", ["n"] = "N", ["float"] = "N", ["numeric"] = "N",
        ["inverse"] = "inv", ["eye"] = "identity", ["eigenvals"] = "eigenvalues", ["eig"] = "eigenvalues",
        ["eigenvects"] = "eigenvectors", ["ln"] = "log", ["arcsin"] = "asin", ["arccos"] = "acos", ["arctan"] = "atan",
        ["arcsinh"] = "asinh", ["arccosh"] = "acosh", ["arctanh"] = "atanh", ["fact"] = "factorial", ["nCr"] = "binomial",
        ["choose"] = "binomial", ["ceiling"] = "ceil", ["tex"] = "latex", ["TeX"] = "latex", ["fsolve"] = "nsolve",
        ["roots"] = "solve", ["partialfractions"] = "apart", ["parfrac"] = "apart", ["length"] = "len", ["size"] = "dims",
        ["fib"] = "fibonacci", ["arg"] = "arg", ["sgn"] = "sign", ["conjugate"] = "conj", ["ifactor"] = "factorint",
        ["primefactors"] = "factorint", ["odesolve"] = "dsolve", ["avg"] = "mean", ["std"] = "stdev",
        ["linspace"] = "range", ["seq"] = "table", ["nIntegrate"] = "nintegrate", ["quad"] = "nintegrate",
        ["?"] = "help",
    };

    public static IEnumerable<Builtin> All => Table.Values.OrderBy(b => b.Category).ThenBy(b => b.Name);

    public static string Canonical(string name) => Aliases.TryGetValue(name, out var c) ? c : name;
    public static bool IsBuiltin(string name) => Table.ContainsKey(Canonical(name));
    public static bool TryGet(string name, out Builtin b) => Table.TryGetValue(Canonical(name), out b!);

    private static void Add(string name, string category, string usage, string description, string example,
        int min, int max, Func<Kernel, Expr[], Expr> handler, params int[] bound) =>
        Table[name] = new Builtin(name, category, usage, description, example, min, max, bound, handler);

    private static void AddLazy(string name, string category, string usage, string description, string example,
        int min, int max, Func<Kernel, List<Expr>, Expr> handler) =>
        Table[name] = new Builtin(name, category, usage, description, example, min, max, [], null, handler);

    // ---- argument helpers ---------------------------------------------------------------------------

    private static Sym Var(Expr e, string fn)
    {
        if (e is Sym s && !s.IsSpecial) return s;
        throw new MathException(Messages.T("{0}: expected a variable name but got {1}. (Has it been assigned a value? Use clear({1}).)", fn, e));
    }

    private static int Int(Expr e, string what)
    {
        if (e is Num { Value.IsInteger: true } n && BigInteger.Abs(n.Value.Num) < int.MaxValue) return (int)n.Value.Num;
        if (e is Real r && r.Value == Math.Floor(r.Value) && Math.Abs(r.Value) < int.MaxValue) return (int)r.Value;
        throw new MathException(Messages.T("{0} must be an integer, got {1}.", Messages.T(what), e));
    }

    private static double Dbl(Expr e, string what)
    {
        if (e.IsConstant && !e.IsUndefinedOrInfinite)
        {
            var v = NumericEval.EvalReal(e);
            if (!double.IsNaN(v)) return v;
        }
        if (e == Expr.Infinity) return double.PositiveInfinity;
        if (e.IsInfinite && e.HasNegativeCoefficient) return double.NegativeInfinity;
        throw new MathException(Messages.T("{0} must be a real number, got {1}.", Messages.T(what), e));
    }

    private static List<Expr> Items(Expr e, string fn) =>
        e is ListExpr l ? [.. l.Items] : throw new MathException(Messages.T("{0}: expected a list, got {1}.", fn, e));

    /// <summary>Accepts (x, a, b) or ([x, a, b]) starting at index i.</summary>
    private static (Sym Var, Expr A, Expr B, int Next) Range(Expr[] a, int i, string fn)
    {
        if (a.Length > i && a[i] is ListExpr { Items.Count: 3 } l)
            return (Var(l.Items[0], fn), l.Items[1], l.Items[2], i + 1);
        if (a.Length < i + 3) throw new MathException(Messages.T("{0}: expected a variable and a range, e.g. {0}(..., x, 0, 1).", fn));
        return (Var(a[i], fn), a[i + 1], a[i + 2], i + 3);
    }

    private static Expr Bool(bool b) => new Sym(b ? "True" : "False");

    private static Expr Simplified(Expr e) => Simplifier.Simplify(e);

    // ---- registration -----------------------------------------------------------------------------

    static Builtins()
    {
        const string Calc = "Calculus", Alg = "Algebra", Num = "Numeric", LinAlg = "Linear algebra",
            NT = "Number theory", Lists = "Lists & statistics", Plot = "Plotting", Util = "Utility";

        // Calculus
        Add("diff", Calc, "diff(f, x)  ·  diff(f, x, n)  ·  diff(f, x, y)", "Derivative of f with respect to x (n-th derivative, or mixed partials).",
            "diff(sin(x)^2, x)", 2, 10, (k, a) =>
            {
                var f = a[0];
                if (a.Length == 3 && a[2] is Num) return Derivative.Diff(f, Var(a[1], "diff"), Int(a[2], "Derivative order"));
                foreach (var v in a.Skip(1)) f = Derivative.Diff(f, Var(v, "diff"));
                return f;
            }, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        Add("integrate", Calc, "integrate(f, x)  ·  integrate(f, x, a, b)", "Indefinite or definite integral. Definite integrals fall back to numeric quadrature when needed.",
            "integrate(x*exp(x), x)", 2, 4, (k, a) =>
            {
                if (a.Length == 2 && a[1] is ListExpr) { var (v0, lo, hi, _) = Range(a, 1, "integrate"); return Integrator.Definite(a[0], v0, lo, hi); }
                var x = Var(a[1], "integrate");
                if (a.Length == 4) return Integrator.Definite(a[0], x, a[2], a[3]);
                if (a.Length == 3) throw new MathException(Messages.T("integrate: give both limits, e.g. integrate(f, x, 0, 1)."));
                return Integrator.Antiderivative(a[0], x) ?? FnExpr.Inert("integrate", a[0], x);
            }, 1);
        Add("nintegrate", Calc, "nintegrate(f, x, a, b)", "Numerical integration (adaptive Gauss–Kronrod; infinite limits allowed).",
            "nintegrate(exp(-x^2), x, -oo, oo)", 2, 4, (k, a) =>
            {
                var (x, lo, hi, _) = Range(a, 1, "nintegrate");
                var f = NumericEval.Compile1(a[0], x.Name);
                var (v, err) = NumericMethods.Integrate(f, Dbl(lo, "Lower limit"), Dbl(hi, "Upper limit"));
                if (!double.IsFinite(v) || err > 1e-3 * (1 + Math.Abs(v))) throw new MathException(Messages.T("The integral does not appear to converge."));
                return new Real(v);
            }, 1);
        Add("limit", Calc, "limit(f, x, a)  ·  limit(f, x, a, \"+\")", "Limit of f as x → a (two-sided, or one-sided with \"+\" / \"-\"). a may be oo or -oo.",
            "limit(sin(x)/x, x, 0)", 3, 4, (k, a) =>
            {
                int dir = 0;
                if (a.Length == 4)
                {
                    dir = a[3] switch
                    {
                        StrExpr { Value: "+" or "right" } => 1,
                        StrExpr { Value: "-" or "left" } => -1,
                        Sym { Name: "right" or "plus" } => 1,
                        Sym { Name: "left" or "minus" } => -1,
                        Num n when n.Value.Sign > 0 => 1,
                        Num n when n.Value.Sign < 0 => -1,
                        _ => throw new MathException(Messages.T("limit: direction must be \"+\" or \"-\".")),
                    };
                }
                return Limits.Limit(a[0], Var(a[1], "limit"), a[2], dir);
            }, 1);
        Add("series", Calc, "series(f, x, a, n)", "Taylor/Laurent series of f about x = a up to order n (default a = 0, n = 6).",
            "series(exp(x)*sin(x), x, 0, 6)", 2, 4, (k, a) =>
                Series.Taylor(a[0], Var(a[1], "series"), a.Length > 2 ? a[2] : Expr.Zero, a.Length > 3 ? Int(a[3], "Order") : 6), 1);
        Add("sum", Calc, "sum(f, k, a, b)  ·  sum(list)", "Sum of f for k from a to b (b may be oo or symbolic), or the total of a list.",
            "sum(1/k^2, k, 1, oo)", 1, 4, (k, a) =>
            {
                if (a.Length == 1) return Expr.Plus(Items(a[0], "sum"));
                var (v, lo, hi, _) = Range(a, 1, "sum");
                return Sums.Sum(a[0], v, lo, hi);
            }, 1);
        Add("product", Calc, "product(f, k, a, b)", "Product of f for k from a to b.", "product(k, k, 1, 10)", 2, 4, (k, a) =>
        {
            var (v, lo, hi, _) = Range(a, 1, "product");
            return Sums.Product(a[0], v, lo, hi);
        }, 1);
        Add("gradient", Calc, "gradient(f, [x, y, ...])", "Vector of partial derivatives.", "gradient(x^2*y + sin(y), [x, y])", 2, 2,
            (k, a) => Expr.List(Items(a[1], "gradient").Select(v => Derivative.Diff(a[0], Var(v, "gradient")))), 1);
        Add("hessian", Calc, "hessian(f, [x, y, ...])", "Matrix of second partial derivatives.", "hessian(x^3*y^2, [x, y])", 2, 2,
            (k, a) =>
            {
                var vars = Items(a[1], "hessian").Select(v => Var(v, "hessian")).ToList();
                return Expr.List(vars.Select(u => Expr.List(vars.Select(w => Derivative.Diff(Derivative.Diff(a[0], u), w)))));
            }, 1);
        Add("jacobian", Calc, "jacobian([f, g, ...], [x, y, ...])", "Jacobian matrix of a vector function.", "jacobian([x*y, x+y], [x, y])", 2, 2,
            (k, a) =>
            {
                var vars = Items(a[1], "jacobian").Select(v => Var(v, "jacobian")).ToList();
                return Expr.List(Items(a[0], "jacobian").Select(f => Expr.List(vars.Select(v => Derivative.Diff(f, v)))));
            }, 1);
        Add("divergence", Calc, "divergence([F1, F2, ...], [x, y, ...])", "Divergence of a vector field.", "divergence([x^2, y^2], [x, y])", 2, 2,
            (k, a) =>
            {
                var f = Items(a[0], "divergence"); var vars = Items(a[1], "divergence");
                if (f.Count != vars.Count) throw new MathException(Messages.T("divergence: field and variables must have the same length."));
                return Expr.Plus(f.Zip(vars, (fi, v) => Derivative.Diff(fi, Var(v, "divergence"))));
            }, 1);
        Add("laplacian", Calc, "laplacian(f, [x, y, ...])", "Sum of unmixed second derivatives.", "laplacian(x^2 + y^2, [x, y])", 2, 2,
            (k, a) => Expr.Plus(Items(a[1], "laplacian").Select(v => Derivative.Diff(a[0], Var(v, "laplacian"), 2))), 1);
        Add("dsolve", Calc, "dsolve(ode, y, x)  ·  dsolve([ode, y(0) = 1], y, x)", "Solves ordinary differential equations (separable, linear first order, linear constant-coefficient). Write derivatives as y', y''.",
            "dsolve(y'' + y = 0, y, x)", 3, 3, (k, a) => Ode.Solve(a[0], Var(a[1], "dsolve"), Var(a[2], "dsolve")), 1, 2);

        // Algebra
        Add("simplify", Alg, "simplify(expr)  ·  simplify(expr, x > 0, ...)", "Tries algebraic, rational and trigonometric transformations and returns the simplest form (optionally under extra assumptions).",
            "simplify(sqrt(x^2), x > 0)", 1, 10, (k, a) =>
                a.Length == 1 ? Simplified(a[0]) : WithConditions(a.Skip(1), () => Simplified(Assumptions.Refine(a[0]))));

        // Checking worked solutions
        const string CheckCategory = "Checking";
        Add("check", CheckCategory, "check([\"step 1\", \"step 2\", ...])", "Checks a worked solution line by line: each step must follow from the previous one, and the final answer must solve the first line (an equation, system, inequality or expression to transform).",
            "check([\"2x + 3 = 7\", \"2x = 4\", \"x = 2\"])", 1, 1, (k, a) =>
                new StrExpr(Checking.WorkChecker.Check(StepTexts(a[0], "check")).ToText()));
        Add("checklatex", CheckCategory, "checklatex([\"step 1\", ...])", "Like check, with the steps written in LaTeX.",
            "checklatex([\"x^2 - 5x + 6 = 0\", \"(x - 2)(x - 3) = 0\", \"x = 2 \\\\text{ or } x = 3\"])", 1, 1, (k, a) =>
                new StrExpr(Checking.WorkChecker.Check(StepTexts(a[0], "checklatex").SelectMany(Parsing.LatexConverter.Convert).ToList()).ToText()));

        // Assumptions
        const string Assume = "Assumptions";
        Add("assume", Assume, "assume(x > 0, ...)  ·  assume(x, real)", "Declares assumptions about variables (inequalities with a number, or real, integer, positive, negative, nonnegative, nonpositive, nonzero, natural). They refine results from then on.",
            "assume(x > 0)", 1, 10, (k, a) =>
            {
                var set = k.Assumptions;
                if (a.Length == 2 && a[0] is Sym s && a[1] is Sym { IsSpecial: false } prop && !s.IsSpecial)
                    set = set.AddProperty(s, prop.Name);
                else
                    foreach (var c in a)
                    {
                        if (c is Sym { Name: "False" }) throw new MathException(Messages.T("assume: this contradicts the current assumptions or values."));
                        set = set.Add(c);
                    }
                k.Assumptions = set;
                return new StrExpr(Messages.T("Assuming {0}.", string.Join("; ", set.Describe())));
            });
        AddLazy("forget", Assume, "forget(x, ...)  ·  forget()", "Removes assumptions about the given variables (all of them without arguments).", "forget(x)", 0, 10, (k, raw) =>
        {
            if (raw.Count == 0) { k.Assumptions = AssumptionSet.Empty; return new StrExpr(Messages.T("All assumptions removed.")); }
            var set = k.Assumptions;
            foreach (var r in raw)
            {
                if (r is not Sym s) throw new MathException(Messages.T("forget: expected variable names."));
                set = set.Without(s.Name);
            }
            k.Assumptions = set;
            return new StrExpr(Messages.T("Forgot assumptions about {0}.", string.Join(", ", raw)));
        });
        Add("assumptions", Assume, "assumptions()", "Lists the assumptions in effect.", "assumptions()", 0, 0, (k, a) =>
        {
            var lines = k.Assumptions.Describe().ToList();
            return new StrExpr(lines.Count == 0 ? Messages.T("No assumptions.") : string.Join("\n", lines));
        });
        Add("refine", Assume, "refine(expr, x > 0, ...)", "Re-evaluates an expression under the current (plus extra) assumptions: sqrt(x^2) → x, abs, sign, (−1)^(2n), sin(n π), ...",
            "refine(sqrt(x^2)*abs(y), x > 0, y < 0)", 1, 10, (k, a) => WithConditions(a.Skip(1), () => Assumptions.Refine(a[0])));
        Add("is", Assume, "is(condition)", "Decides a condition from the assumptions: True, False or Unknown.", "is(x^2 + 1 > 0)", 1, 1, (k, a) =>
            a[0] is Sym { Name: "True" or "False" } ? a[0] : new Sym("Unknown"));
        Add("expand", Alg, "expand(expr)", "Multiplies out products and powers.", "expand((x + 1)^3)", 1, 1, (k, a) => Expansion.Expand(a[0]));
        Add("factor", Alg, "factor(expr)", "Factors polynomials and rational functions over the rationals.", "factor(x^3 - 6x^2 + 11x - 6)", 1, 1,
            (k, a) => Factoring.Factor(a[0]));
        Add("sqrfree", Alg, "sqrfree(p)", "Square-free factorisation: a product of powers of pairwise coprime square-free polynomials.",
            "sqrfree(x^3 - x^2 - x + 1)", 1, 1, (k, a) => Factoring.SquareFreeFactor(a[0]));
        Add("polydiv", Alg, "polydiv(p, q, x)", "Polynomial division in x: [quotient, remainder] with p = quotient·q + remainder.",
            "polydiv(x^3 + 2x + 1, x^2 + 1, x)", 2, 3, (k, a) =>
            {
                var (q, r) = PolyDivide(a[0], a[1], a.Length > 2 ? Var(a[2], "polydiv") : MainVariable(a[0], a[1], "polydiv"));
                return Expr.List(q, r);
            }, 2);
        Add("quo", Alg, "quo(p, q, x)", "Quotient of polynomial division in x.", "quo(x^3 + 2x + 1, x^2 + 1, x)", 2, 3, (k, a) =>
            PolyDivide(a[0], a[1], a.Length > 2 ? Var(a[2], "quo") : MainVariable(a[0], a[1], "quo")).Quotient, 2);
        Add("rem", Alg, "rem(p, q, x)", "Remainder of polynomial division in x.", "rem(x^3 + 2x + 1, x^2 + 1, x)", 2, 3, (k, a) =>
            PolyDivide(a[0], a[1], a.Length > 2 ? Var(a[2], "rem") : MainVariable(a[0], a[1], "rem")).Remainder, 2);
        Add("resultant", Alg, "resultant(p, q, x)", "Resultant of two polynomials in x (zero exactly when they share a root).",
            "resultant(x^2 - a, x - b, x)", 3, 3, (k, a) =>
            {
                var x = Var(a[2], "resultant");
                var cp = PolyView.Coefficients(a[0], x) ?? throw new MathException(Messages.T("resultant: not a polynomial in {0}.", x));
                var cq = PolyView.Coefficients(a[1], x) ?? throw new MathException(Messages.T("resultant: not a polynomial in {0}.", x));
                return Factoring.Factor(Expansion.Expand(Solver.Resultant(cp, cq)));
            }, 2);
        Add("discriminant", Alg, "discriminant(p, x)", "Discriminant of a polynomial in x (zero exactly when it has a repeated root).",
            "discriminant(a*x^2 + b*x + c, x)", 2, 2, (k, a) =>
            {
                var x = Var(a[1], "discriminant");
                var cp = PolyView.Coefficients(a[0], x) ?? throw new MathException(Messages.T("discriminant: not a polynomial in {0}.", x));
                int n = cp.Count - 1;
                if (n < 1) throw new MathException(Messages.T("discriminant: the polynomial must have positive degree."));
                var dp = PolyView.Coefficients(Calculus.Derivative.Diff(a[0], x), x)!;
                var sign = (n * (n - 1) / 2) % 2 == 0 ? Expr.One : Expr.MinusOne;
                return Factoring.Factor(Rational.Cancel(sign * Solver.Resultant(cp, dp) / cp[n]));
            }, 1);
        Add("together", Alg, "together(expr)", "Combines a sum of fractions over a common denominator.", "together(1/x + 1/(x + 1))", 1, 1,
            (k, a) => Rational.Together(a[0]));
        Add("cancel", Alg, "cancel(expr)", "Cancels common factors between numerator and denominator.", "cancel((x^2 - 1)/(x - 1))", 1, 1,
            (k, a) => Rational.Cancel(a[0]));
        Add("apart", Alg, "apart(expr, x)", "Partial fraction decomposition.", "apart(1/(x^2 - 1), x)", 1, 2, (k, a) =>
        {
            var x = a.Length > 1 ? Var(a[1], "apart") : a[0].FreeSymbols().FirstOrDefault() ?? throw new MathException(Messages.T("apart: no variable."));
            return Rational.Apart(a[0], x);
        }, 1);
        Add("collect", Alg, "collect(expr, x)", "Groups terms by powers of x.", "collect(a*x + b*x + c, x)", 2, 2, (k, a) =>
        {
            var x = Var(a[1], "collect");
            var c = PolyView.Coefficients(a[0], x) ?? throw new MathException(Messages.T("collect: expression is not a polynomial in {0}.", x));
            return Expr.Plus(c.Select((ci, i) => Factoring.Factor(ci) * Expr.Power(x, i)));
        }, 1);
        Add("coeffs", Alg, "coeffs(p, x)", "Coefficients of a polynomial, from the constant term upwards.", "coeffs(3x^2 + 2x + 1, x)", 2, 2, (k, a) =>
            Expr.List(PolyView.Coefficients(a[0], Var(a[1], "coeffs")) ?? throw new MathException(Messages.T("coeffs: not a polynomial."))), 1);
        Add("degree", Alg, "degree(p, x)", "Degree of a polynomial in x.", "degree(x^5 + x, x)", 2, 2, (k, a) =>
        {
            var d = PolyView.Degree(a[0], Var(a[1], "degree"));
            if (d < 0) throw new MathException(Messages.T("degree: not a polynomial."));
            return d;
        }, 1);
        Add("numer", Alg, "numer(expr)", "Numerator after combining fractions.", "numer((x+1)/(x-1))", 1, 1, (k, a) => Rational.NumDen(Rational.Together(a[0])).Num);
        Add("denom", Alg, "denom(expr)", "Denominator after combining fractions.", "denom((x+1)/(x-1))", 1, 1, (k, a) => Rational.NumDen(Rational.Together(a[0])).Den);
        Add("trigsimp", Alg, "trigsimp(expr)", "Simplifies using trigonometric identities.", "trigsimp(tan(x)*cos(x))", 1, 1, (k, a) => Simplifier.TrigSimplify(a[0]));
        Add("solve", Alg, "solve(eq, x)  ·  solve([eq1, eq2], [x, y])", "Solves equations and systems exactly where possible (numerically otherwise).",
            "solve(x^2 - 5x + 6 = 0, x)", 1, 2, (k, a) => Solver.Solve(a[0], a.Length > 1 ? a[1] : null), 1);
        Add("nsolve", Alg, "nsolve(eq, x)  ·  nsolve(eq, x, a, b)", "Real roots found numerically (in [-100, 100] by default, or in [a, b]).",
            "nsolve(cos(x) = x, x)", 2, 4, (k, a) =>
            {
                var x = Var(a[1], "nsolve");
                var f = a[0] is EqExpr q ? q.Lhs - q.Rhs : a[0];
                var lo = a.Length > 2 ? Dbl(a[2], "Lower bound") : -100;
                var hi = a.Length > 3 ? Dbl(a[3], "Upper bound") : 100;
                return Expr.List(Solver.NumericSolve(f, x, lo, hi).Select(r => Expr.Equation(x, r)));
            }, 1);
        Add("subs", Alg, "subs(expr, x = a)  ·  subs(expr, [x = a, y = b])  ·  subs(expr, x, a)", "Substitutes values for variables.",
            "subs(x^2 + y, x = 3)", 2, 3, (k, a) =>
            {
                if (a.Length == 3) return a[0].Substitute(a[1], a[2]);
                var rules = a[1] is ListExpr l ? l.Items : [a[1]];
                var map = new Dictionary<Expr, Expr>();
                foreach (var r in rules)
                {
                    if (r is not EqExpr eq) throw new MathException(Messages.T("subs: use equations like x = 2."));
                    map[eq.Lhs] = eq.Rhs;
                }
                return a[0].Substitute(map);
            });
        Add("lhs", Alg, "lhs(eq)", "Left-hand side of an equation.", "lhs(x^2 = 4)", 1, 1, (k, a) => a[0] is EqExpr q ? q.Lhs : throw new MathException(Messages.T("lhs: not an equation.")));
        Add("rhs", Alg, "rhs(eq)", "Right-hand side of an equation.", "rhs(x^2 = 4)", 1, 1, (k, a) => a[0] is EqExpr q ? q.Rhs : throw new MathException(Messages.T("rhs: not an equation.")));

        // Numeric
        Add("N", Num, "N(expr)  ·  N(expr, digits)", "Numerical value: machine precision, or the given number of significant digits.",
            "N(pi, 50)", 1, 2, (k, a) =>
            {
                if (a.Length == 1) return ToNumeric(a[0]);
                int digits = Int(a[1], "Digits");
                if (digits < 1 || digits > 20000) throw new MathException(Messages.T("N: digits must be between 1 and 20000."));
                return ToNumeric(a[0], digits);
            });
        Add("identify", Num, "identify(x)", "Recognises a decimal number as an exact constant (rational, algebraic of small degree, combinations of π, e, logs and square roots).",
            "identify(N(sqrt(2) + pi, 40))", 1, 1, (k, a) =>
            {
                var x = a[0];
                var (value, digits) = x switch
                {
                    Real { Big: { } b } r => (b, r.Digits),
                    Real r => (BigFloat.FromDouble(r.Value, 64), 15),
                    _ when x.IsConstant && !x.IsUndefinedOrInfinite => (BigEval.Eval(x, BigFloat.DigitsToBits(60)).Re, 60),
                    _ => throw new MathException(Messages.T("identify: expected a number.")),
                };
                return Identify.Constant(value, digits) ?? x;
            });
        Add("re", Num, "re(z)", "Real part.", "re(3 + 4I)", 1, 1, (k, a) => ComplexPart(a[0], true));
        Add("im", Num, "im(z)", "Imaginary part.", "im(3 + 4I)", 1, 1, (k, a) => ComplexPart(a[0], false));

        // Number theory
        Add("factorint", NT, "factorint(n)", "Prime factorisation as [prime, exponent] pairs.", "factorint(360)", 1, 1,
            (k, a) => Factoring.FactorInteger(BigIntArg(a[0], "factorint")));
        Add("isprime", NT, "isprime(n)", "Primality test.", "isprime(2^31 - 1)", 1, 1, (k, a) => Bool(IntMath.IsPrime(BigIntArg(a[0], "isprime"))));
        Add("nextprime", NT, "nextprime(n)", "Smallest prime greater than n.", "nextprime(100)", 1, 1, (k, a) =>
        {
            var n = BigIntArg(a[0], "nextprime") + 1;
            while (!IntMath.IsPrime(n)) n++;
            return new Num(n);
        });
        Add("prime", NT, "prime(n)", "The n-th prime number.", "prime(100)", 1, 1, (k, a) =>
        {
            int n = Int(a[0], "n");
            if (n < 1 || n > 2_000_000) throw new MathException(Messages.T("prime(n) needs 1 ≤ n ≤ 2000000."));
            // p_n < n (ln n + ln ln n) for n >= 6
            int limit = n < 6 ? 15 : (int)(n * (Math.Log(n) + Math.Log(Math.Log(n)))) + 1;
            var composite = new bool[limit + 1];
            int count = 0;
            for (int p = 2; p <= limit; p++)
            {
                if (composite[p]) continue;
                if (++count == n) return new Num(p);
                for (long m = (long)p * p; m <= limit; m += p) composite[m] = true;
            }
            throw new MathException(Messages.T("prime(n): internal bound exceeded."));
        });
        Add("divisors", NT, "divisors(n)", "Positive divisors.", "divisors(60)", 1, 1, (k, a) => Expr.List(IntMath.Divisors(BigIntArg(a[0], "divisors")).Select(d => (Expr)new Num(d))));
        Add("fibonacci", NT, "fibonacci(n)", "The n-th Fibonacci number.", "fibonacci(50)", 1, 1, (k, a) =>
        {
            int n = Int(a[0], "n");
            if (n < 0 || n > 100000) throw new MathException(Messages.T("fibonacci(n) needs 0 ≤ n ≤ 100000."));
            BigInteger x = 0, y = 1;
            for (int i = 0; i < n; i++) (x, y) = (y, x + y);
            return new Num(x);
        });

        // Linear algebra
        Add("det", LinAlg, "det(A)", "Determinant.", "det([[1, 2], [3, 4]])", 1, 1, (k, a) => LinearAlgebra.Determinant(a[0]));
        Add("inv", LinAlg, "inv(A)", "Matrix inverse.", "inv([[2, 1], [1, 1]])", 1, 1, (k, a) => LinearAlgebra.Inverse(a[0]));
        Add("transpose", LinAlg, "transpose(A)", "Transpose.", "transpose([[1, 2, 3], [4, 5, 6]])", 1, 1, (k, a) => LinearAlgebra.Transpose(a[0]));
        Add("rank", LinAlg, "rank(A)", "Rank of a matrix.", "rank([[1, 2], [2, 4]])", 1, 1, (k, a) => LinearAlgebra.Rank(a[0]));
        Add("rref", LinAlg, "rref(A)", "Reduced row echelon form.", "rref([[1, 2, 3], [4, 5, 6]])", 1, 1, (k, a) => LinearAlgebra.FromArray(LinearAlgebra.Rref(LinearAlgebra.ToArray(a[0])).Matrix));
        Add("trace", LinAlg, "trace(A)", "Sum of the diagonal.", "trace([[1, 2], [3, 4]])", 1, 1, (k, a) =>
        {
            var m = LinearAlgebra.ToArray(a[0]);
            return Expr.Plus(Enumerable.Range(0, Math.Min(m.GetLength(0), m.GetLength(1))).Select(i => m[i, i]));
        });
        Add("nullspace", LinAlg, "nullspace(A)", "Basis of the null space.", "nullspace([[1, 2], [2, 4]])", 1, 1, (k, a) => Expr.List(LinearAlgebra.NullSpace(LinearAlgebra.ToArray(a[0]))));
        Add("eigenvalues", LinAlg, "eigenvalues(A)", "Eigenvalues (with multiplicity).", "eigenvalues([[2, 1], [1, 2]])", 1, 1, (k, a) => Expr.List(LinearAlgebra.Eigenvalues(a[0])));
        Add("eigenvectors", LinAlg, "eigenvectors(A)", "List of [eigenvalue, multiplicity, [vectors]].", "eigenvectors([[2, 1], [1, 2]])", 1, 1, (k, a) => LinearAlgebra.Eigenvectors(a[0]));
        Add("charpoly", LinAlg, "charpoly(A, x)", "Characteristic polynomial det(x·I − A).", "charpoly([[1, 2], [3, 4]], x)", 1, 2, (k, a) =>
            LinearAlgebra.CharacteristicPolynomial(a[0], a.Length > 1 ? Var(a[1], "charpoly") : new Sym("x")), 1);
        Add("identity", LinAlg, "identity(n)", "n×n identity matrix.", "identity(3)", 1, 1, (k, a) => LinearAlgebra.Identity(Int(a[0], "n")));
        Add("zeros", LinAlg, "zeros(m, n)", "Matrix of zeros.", "zeros(2, 3)", 1, 2, (k, a) => Filled(a, Expr.Zero));
        Add("ones", LinAlg, "ones(m, n)", "Matrix of ones.", "ones(2, 2)", 1, 2, (k, a) => Filled(a, Expr.One));
        Add("diag", LinAlg, "diag([a, b, c])", "Diagonal matrix from a list (or the diagonal of a matrix).", "diag([1, 2, 3])", 1, 1, (k, a) =>
        {
            if (LinearAlgebra.IsMatrix(a[0]))
            {
                var m = LinearAlgebra.ToArray(a[0]);
                return Expr.List(Enumerable.Range(0, Math.Min(m.GetLength(0), m.GetLength(1))).Select(i => m[i, i]));
            }
            var items = Items(a[0], "diag");
            return Expr.List(items.Select((v, i) => Expr.List(items.Select((_, j) => i == j ? v : Expr.Zero))));
        });
        Add("dot", LinAlg, "dot(u, v)", "Dot product (or matrix product).", "dot([1, 2, 3], [4, 5, 6])", 2, 2, (k, a) => LinearAlgebra.Dot(a[0], a[1]));
        Add("cross", LinAlg, "cross(u, v)", "Cross product of 3-vectors.", "cross([1, 0, 0], [0, 1, 0])", 2, 2, (k, a) => LinearAlgebra.Cross(a[0], a[1]));
        Add("norm", LinAlg, "norm(v)", "Euclidean norm of a vector (Frobenius norm of a matrix).", "norm([3, 4])", 1, 1, (k, a) =>
            Simplified(Expr.Sqrt(Expr.Plus(Flatten(a[0]).Select(x => x.IsNumber || x.IsConstant && !x.Contains(Expr.I) ? Expr.Power(x, 2) : Expr.Power(Expr.Call("abs", x), 2))))));
        Add("linsolve", LinAlg, "linsolve(A, b)", "Solves the linear system A·x = b.", "linsolve([[2, 1], [1, 3]], [3, 5])", 2, 2, (k, a) => LinearAlgebra.LinSolve(a[0], a[1]));

        // Lists & statistics
        AddLazy("table", Lists, "table(expr, i, a, b)  ·  table(expr, i, a, b, step)", "List of expr for i from a to b.", "table(i^2, i, 1, 10)", 4, 5, (k, raw) =>
        {
            if (raw[1] is not Sym { IsSpecial: false } i) throw new MathException(Messages.T("table: expected a variable name as the second argument."));
            var lo = k.Eval(raw[2]); var hi = k.Eval(raw[3]); var step = raw.Count > 4 ? k.Eval(raw[4]) : Expr.One;
            double dlo = Dbl(lo, "Start"), dhi = Dbl(hi, "End"), ds = Dbl(step, "Step");
            if (ds == 0 || (dhi - dlo) / ds > 100000) throw new MathException(Messages.T("table: too many elements."));
            var items = new List<Expr>();
            int count = (int)Math.Floor((dhi - dlo) / ds + 1e-9) + 1;
            // Evaluate the body for each value, so that commands like prime(n) see a number.
            for (int j = 0; j < count; j++)
                items.Add(k.EvalWith(i.Name, lo + step * j, raw[0]));
            return Expr.List(items);
        });
        Add("range", Lists, "range(n)  ·  range(a, b)  ·  range(a, b, step)", "Arithmetic sequence as a list.", "range(0, 1, 1/4)", 1, 3, (k, a) =>
        {
            Expr lo = a.Length == 1 ? Expr.One : a[0], hi = a.Length == 1 ? a[0] : a[1], step = a.Length > 2 ? a[2] : Expr.One;
            double dlo = Dbl(lo, "Start"), dhi = Dbl(hi, "End"), ds = Dbl(step, "Step");
            if (ds == 0 || (dhi - dlo) / ds > 100000) throw new MathException(Messages.T("range: too many elements."));
            int count = (int)Math.Floor((dhi - dlo) / ds + 1e-9) + 1;
            return Expr.List(Enumerable.Range(0, Math.Max(0, count)).Select(j => lo + step * j));
        });
        Add("len", Lists, "len(list)", "Number of elements.", "len([1, 2, 3])", 1, 1, (k, a) => Items(a[0], "len").Count);
        Add("dims", Lists, "dims(A)", "Dimensions of a list or matrix.", "dims([[1, 2, 3], [4, 5, 6]])", 1, 1, (k, a) =>
            LinearAlgebra.IsMatrix(a[0]) ? Expr.List(Items(a[0], "dims").Count, Items(Items(a[0], "dims")[0], "dims").Count) : Expr.List(Items(a[0], "dims").Count));
        Add("total", Lists, "total(list)", "Sum of the elements.", "total([1, 2, 3])", 1, 1, (k, a) => Expr.Plus(Items(a[0], "total")));
        Add("mean", Lists, "mean(list)", "Arithmetic mean.", "mean([1, 2, 3, 10])", 1, 1, (k, a) =>
        {
            var l = Items(a[0], "mean");
            if (l.Count == 0) throw new MathException(Messages.T("mean of an empty list."));
            return Expr.Plus(l) / l.Count;
        });
        Add("median", Lists, "median(list)", "Median of numbers.", "median([3, 1, 4, 1, 5])", 1, 1, (k, a) =>
        {
            var l = Items(a[0], "median").OrderBy(x => Dbl(x, "median element")).ToList();
            if (l.Count == 0) throw new MathException(Messages.T("median of an empty list."));
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        });
        Add("variance", Lists, "variance(list)", "Sample variance.", "variance([1, 2, 3, 4])", 1, 1, (k, a) => Variance(Items(a[0], "variance")));
        Add("stdev", Lists, "stdev(list)", "Sample standard deviation.", "stdev([1, 2, 3, 4])", 1, 1, (k, a) => Expr.Sqrt(Variance(Items(a[0], "stdev"))));
        Add("sort", Lists, "sort(list)", "Sorts numbers ascending.", "sort([3, 1, 2])", 1, 1, (k, a) =>
            Expr.List(Items(a[0], "sort").OrderBy(x => x.IsConstant && !x.IsUndefinedOrInfinite ? NumericEval.EvalReal(x) : double.MaxValue).ThenBy(x => x)));
        Add("reverse", Lists, "reverse(list)", "Reverses a list.", "reverse([1, 2, 3])", 1, 1, (k, a) => Expr.List(Enumerable.Reverse(Items(a[0], "reverse"))));
        Add("flatten", Lists, "flatten(list)", "Flattens nested lists.", "flatten([[1, 2], [3]])", 1, 1, (k, a) => Expr.List(Flatten(a[0])));
        Add("max", Lists, "max(a, b, ...)  ·  max(list)", "Largest value.", "max(3, pi, 2)", 1, int.MaxValue, (k, a) => Extreme(a, true));
        Add("min", Lists, "min(a, b, ...)  ·  min(list)", "Smallest value.", "min([3, pi, 2])", 1, int.MaxValue, (k, a) => Extreme(a, false));

        // Plotting
        Add("plot", Plot, "plot(f, x, a, b)  ·  plot([f, g], x, a, b)", "2D plot of one or more functions of x. Drag to pan, scroll to zoom.",
            "plot([sin(x), cos(x)], x, -2pi, 2pi)", 2, 6, (k, a) => Plots.Plot2D(a), 1);
        Add("parametricplot", Plot, "parametricplot([fx, fy], t, a, b)", "Parametric curve (x(t), y(t)).",
            "parametricplot([cos(3t), sin(2t)], t, 0, 2pi)", 4, 4, (k, a) => Plots.Parametric(a), 1);
        Add("polarplot", Plot, "polarplot(r, theta, a, b)", "Polar curve r(θ).", "polarplot(1 + cos(t), t, 0, 2pi)", 4, 4, (k, a) => Plots.Polar(a), 1);
        Add("listplot", Plot, "listplot(list)  ·  listplot([[x1, y1], ...])", "Plots points.", "listplot(table(prime(n), n, 1, 30))", 1, 2, (k, a) => Plots.ListPlot(a));
        Add("plot3d", Plot, "plot3d(f, x, a, b, y, c, d)", "Surface plot of f(x, y). Drag to rotate.",
            "plot3d(sin(x)*cos(y), x, -pi, pi, y, -pi, pi)", 7, 7, (k, a) => Plots.Plot3D(a), 1, 4);

        // Utility
        Add("latex", Util, "latex(expr)", "LaTeX source of an expression.", "latex(integrate(1/(1+x^2), x))", 1, 1, (k, a) => new StrExpr(LatexFormatter.Format(a[0])));
        Add("text", Util, "text(expr)", "Linear text form of an expression.", "text(sqrt(x)/2)", 1, 1, (k, a) => new StrExpr(TextFormatter.Format(a[0])));
        AddLazy("clear", Util, "clear(x, ...)", "Removes values/definitions of variables or functions.", "clear(x)", 1, int.MaxValue, (k, raw) =>
        {
            foreach (var r in raw)
            {
                if (r is not Sym s) throw new MathException(Messages.T("clear: expected names."));
                k.Clear(s.Name);
            }
            return new StrExpr(Messages.T("Cleared {0}.", string.Join(", ", raw)));
        });
        AddLazy("clearall", Util, "clearall()", "Removes all variables, functions and history.", "clearall()", 0, 0, (k, raw) =>
        {
            k.Reset();
            return new StrExpr(Messages.T("All definitions cleared."));
        });
        AddLazy("if", Util, "if(condition, then, else)", "Conditional evaluation (useful in recursive definitions).", "f(n) := if(n <= 1, 1, n*f(n - 1))", 2, 3, (k, raw) =>
        {
            var c = k.Eval(raw[0]);
            if (c is Sym { Name: "True" } || (c.IsNumber && !c.IsZero)) return k.Eval(raw[1]);
            if (c is Sym { Name: "False" } || c.IsZero) return raw.Count > 2 ? k.Eval(raw[2]) : new Sym("Null");
            throw new MathException(Messages.T("if: the condition could not be decided (it may contain unassigned symbols)."));
        });
        AddLazy("help", Util, "help(name)  ·  help()", "Describes a command, or lists all commands.", "help(integrate)", 0, 1, (k, raw) => new StrExpr(Help(raw)));
        AddLazy("vars", Util, "vars()", "Lists defined variables and functions.", "vars()", 0, 0, (k, raw) =>
        {
            var lines = k.Variables.Select(v => $"{v.Key} = {TextFormatter.Format(v.Value)}")
                .Concat(k.UserFunctions.Values.Select(f => $"{f.Name}({string.Join(", ", f.Parameters)}) := {TextFormatter.Format(k.WithShadowed(f.Parameters, () => k.Eval(f.Body)))}"));
            var text = string.Join("\n", lines);
            return new StrExpr(text.Length == 0 ? Messages.T("No definitions.") : text);
        });
    }

    private static string Help(List<Expr> raw)
    {
        if (raw.Count == 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var g in All.GroupBy(b => b.Category))
            {
                sb.Append(g.Key).Append(": ").AppendLine(string.Join(", ", g.Select(b => b.Name)));
            }
            sb.Append(Messages.T("Use help(name) for details. Assign with :=, write equations with =, end a line with ; to hide its result."));
            return sb.ToString();
        }
        var name = raw[0] switch { Sym s => s.Name, StrExpr str => str.Value, FnExpr { Name: "@call", Args: [Sym fs, ..] } => fs.Name, _ => raw[0].ToString() };
        if (TryGet(name, out var b))
            return $"{b.Usage}\n{Messages.Description(b)}\n" + Messages.T("Example: {0}", b.Example);
        if (Functions.Numeric.Contains(Canonical(name)) || name is "exp" or "sqrt")
            return Messages.T("{0}(x) is a built-in mathematical function.", name);
        return Messages.T("No help for '{0}'.", name);
    }

    private static List<string> StepTexts(Expr e, string fn) => e switch
    {
        ListExpr l when l.Items.All(i => i is StrExpr) => l.Items.Cast<StrExpr>().Select(s => s.Value).ToList(),
        StrExpr s => s.Value.Split('\n').ToList(),
        _ => throw new MathException(Messages.T("{0}: give the steps as a list of strings, e.g. {0}([\"2x + 3 = 7\", \"x = 2\"]).", fn)),
    };

    /// <summary>Runs body with the current assumptions extended by the given conditions.</summary>
    private static Expr WithConditions(IEnumerable<Expr> conditions, Func<Expr> body)
    {
        var set = Assumptions.Current;
        foreach (var c in conditions)
        {
            if (c is Sym { Name: "False" }) throw new MathException(Messages.T("The condition contradicts the current assumptions."));
            set = set.Add(c);
        }
        using (Assumptions.Scope(set)) return body();
    }

    private static Sym MainVariable(Expr p, Expr q, string fn)
    {
        var syms = p.FreeSymbols().Union(q.FreeSymbols()).ToList();
        if (syms.Count == 1) return syms[0];
        throw new MathException(Messages.T("{0}: give the variable, e.g. {0}(p, q, x).", fn));
    }

    /// <summary>Long division in x with coefficients that may involve other symbols (over their fraction field).</summary>
    private static (Expr Quotient, Expr Remainder) PolyDivide(Expr p, Expr q, Sym x)
    {
        var cp = PolyView.Coefficients(p, x) ?? throw new MathException(Messages.T("polydiv: {0} is not a polynomial in {1}.", p, x));
        var cq = PolyView.Coefficients(q, x) ?? throw new MathException(Messages.T("polydiv: {0} is not a polynomial in {1}.", q, x));
        if (cq.Count == 1 && cq[0].IsZero) throw new MathException(Messages.T("polydiv: division by zero."));
        var rem = cp.ToList();
        int dq = cq.Count - 1;
        var quot = new Expr[Math.Max(rem.Count - dq, 1)];
        for (int i = 0; i < quot.Length; i++) quot[i] = Expr.Zero;
        for (int k = rem.Count - 1 - dq; k >= 0; k--)
        {
            var c = Rational.Cancel(rem[k + dq] / cq[dq]);
            quot[k] = c;
            if (c.IsZero) continue;
            for (int j = 0; j <= dq; j++) rem[k + j] = Rational.Cancel(rem[k + j] - c * cq[j]);
        }
        var r = Expr.Plus(rem.Take(dq).Select((c, i) => c * Expr.Power(x, i)));
        return (Expr.Plus(quot.Select((c, i) => c * Expr.Power(x, i))), r);
    }

    private static BigInteger BigIntArg(Expr e, string fn) =>
        e is Num { Value.IsInteger: true } n ? n.Value.Num : throw new MathException(Messages.T("{0}: expected an integer.", fn));

    private static Expr Filled(Expr[] a, Expr value)
    {
        int m = Int(a[0], "Rows"), n = a.Length > 1 ? Int(a[1], "Columns") : m;
        if (m < 1 || n < 1 || (long)m * n > 1_000_000) throw new MathException(Messages.T("Invalid matrix size."));
        return Expr.List(Enumerable.Range(0, m).Select(_ => Expr.List(Enumerable.Repeat(value, n))));
    }

    private static IEnumerable<Expr> Flatten(Expr e) => e is ListExpr l ? l.Items.SelectMany(Flatten) : [e];

    private static Expr Variance(List<Expr> l)
    {
        if (l.Count < 2) throw new MathException(Messages.T("variance needs at least two values."));
        var mean = Expr.Plus(l) / l.Count;
        return Simplified(Expr.Plus(l.Select(x => Expr.Power(x - mean, 2))) / (l.Count - 1));
    }

    private static Expr Extreme(Expr[] a, bool max)
    {
        var items = a.Length == 1 && a[0] is ListExpr l ? l.Items.ToArray() : a;
        if (items.All(x => x.IsConstant && !x.Contains(Expr.I) && !x.IsUndefinedOrInfinite))
        {
            var best = items[0];
            double bv = NumericEval.EvalReal(best);
            foreach (var x in items.Skip(1))
            {
                var v = NumericEval.EvalReal(x);
                if (max ? v > bv : v < bv) { best = x; bv = v; }
            }
            return best;
        }
        return Expr.Call(max ? "max" : "min", items);
    }

    private static Expr ComplexPart(Expr z, bool real)
    {
        var e = Expansion.Expand(z);
        if (e is ListExpr l) return Expr.List(l.Items.Select(i => ComplexPart(i, real)));
        var c = PolyView.Coefficients(e, Expr.I);
        // The split into real and imaginary parts is only valid when every coefficient is real.
        if (c is null || c.Any(t => !t.IsNumber && !Assumptions.IsReal(t))) return Expr.Call(real ? "re" : "im", z);
        // Reduce powers of I: sum c_k I^k
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
        return real ? re : im;
    }

    /// <summary>
    /// Arbitrary-precision numeric value with the given significant digits. Constant subexpressions are
    /// evaluated as a whole (so cancellation is handled at full precision); symbols are kept.
    /// </summary>
    public static Expr ToNumeric(Expr e, int digits)
    {
        switch (e)
        {
            case GraphicsExpr or StrExpr or Sym { IsSpecial: false }: return e;
            case ListExpr or EqExpr: return e.WithArgs(e.Args.Select(x => ToNumeric(x, digits)).ToArray());
            case FnExpr { Name: "@rel" } rel: return FnExpr.Inert("@rel", rel.Args[0], ToNumeric(rel.Args[1], digits), ToNumeric(rel.Args[2], digits));
        }
        if (e.IsConstant && !e.IsUndefinedOrInfinite)
        {
            try
            {
                // Work with extra digits so the requested ones are correct after rounding.
                var bits = BigFloat.DigitsToBits(digits + 10) + 32;
                return BigEval.ToExpr(BigEval.Eval(e, bits), digits);
            }
            catch (MathException) { }
            catch (DivideByZeroException) { return Expr.ComplexInfinity; }
        }
        return e switch
        {
            PowExpr p => Expr.Power(ToNumeric(p.Base, digits), p.Exponent is Num { Value.IsInteger: true } ? p.Exponent : ToNumeric(p.Exponent, digits)),
            _ when e.Args.Count == 0 => e,
            _ => e.WithArgs(e.Args.Select(x => ToNumeric(x, digits)).ToArray()),
        };
    }

    /// <summary>Converts exact numbers and constants to floating point, keeping integer exponents exact.</summary>
    public static Expr ToNumeric(Expr e) => e switch
    {
        Num n => new Real(n.Value.ToDouble()),
        Sym { Name: "Pi" } => new Real(Math.PI),
        Sym { Name: "E" } => new Real(Math.E),
        PowExpr p => Expr.Power(ToNumeric(p.Base), p.Exponent is Num { Value.IsInteger: true } ? p.Exponent : ToNumeric(p.Exponent)),
        GraphicsExpr or StrExpr => e,
        _ when e.Args.Count == 0 => e,
        _ => e.WithArgs(e.Args.Select(x => ToNumeric(x)).ToArray()),
    };
}
