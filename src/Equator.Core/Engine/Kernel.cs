using System.Diagnostics;
using Equator.Core.Algebra;
using Equator.Core.Parsing;
using Equator.Core.Printing;

namespace Equator.Core.Engine;

public sealed record UserFunction(string Name, IReadOnlyList<string> Parameters, Expr Body);

/// <summary>
/// The evaluator: holds variables, user functions and output history, and evaluates parsed statements.
/// A kernel is not thread-safe; callers serialise evaluations.
/// </summary>
public sealed class Kernel
{
    private readonly Dictionary<string, Expr> _vars = new();
    private readonly Dictionary<string, UserFunction> _funcs = new();
    private readonly Dictionary<int, Expr> _out = new();
    private readonly List<Expr> _history = [];
    private readonly HashSet<string> _shadowed = [];
    private AssumptionSet _assumptions = AssumptionSet.Empty;
    private int _depth;

    /// <summary>Assumptions about symbols made with assume(); in effect during every evaluation.</summary>
    public AssumptionSet Assumptions
    {
        get => _assumptions;
        set
        {
            _assumptions = value;
            Engine.Assumptions.SetCurrent(value);
        }
    }

    public int ExecutionCount { get; private set; }

    public IReadOnlyDictionary<string, Expr> Variables => _vars;
    public IReadOnlyDictionary<string, UserFunction> UserFunctions => _funcs;

    /// <summary>Raised after an evaluation changes variables or functions (for UI panels).</summary>
    public event EventHandler? EnvironmentChanged;

    public void Reset()
    {
        _vars.Clear();
        _funcs.Clear();
        _out.Clear();
        _history.Clear();
        _assumptions = AssumptionSet.Empty;
        ExecutionCount = 0;
        EnvironmentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string name)
    {
        _vars.Remove(name);
        _funcs.Remove(name);
    }

    /// <summary>Parses and evaluates a cell's input, returning displayable outputs.</summary>
    public EvaluationResult Evaluate(string input, CancellationToken cancellation = default)
    {
        int index = ++ExecutionCount;
        var sw = Stopwatch.StartNew();
        var outputs = new List<CellOutput>();
        using var _ = Interrupt.Scope(cancellation);
        using var __ = Engine.Assumptions.Scope(_assumptions);

        IReadOnlyList<Statement> statements;
        try
        {
            statements = Parser.ParseProgram(input);
        }
        catch (ParseException ex)
        {
            outputs.Add(new CellOutput(OutputKind.Error, $"Syntax error at position {ex.Position + 1}: {ex.Message}"));
            return new EvaluationResult(index, outputs, sw.Elapsed);
        }

        foreach (var stmt in statements)
        {
            try
            {
                _depth = 0;
                var value = Eval(stmt.Tree);
                Record(index, value);
                if (!stmt.Suppressed) outputs.Add(ToOutput(value));
            }
            catch (OperationCanceledException)
            {
                outputs.Add(new CellOutput(OutputKind.Error, "Evaluation aborted."));
                break;
            }
            catch (MathException ex)
            {
                outputs.Add(new CellOutput(OutputKind.Error, ex.Message));
            }
            catch (DivideByZeroException)
            {
                outputs.Add(new CellOutput(OutputKind.Error, "Division by zero."));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException or IndexOutOfRangeException or NullReferenceException or InvalidCastException or KeyNotFoundException or InsufficientExecutionStackException)
            {
                outputs.Add(new CellOutput(OutputKind.Error, $"Could not evaluate '{stmt.Source}': {ex.Message}"));
            }
        }
        EnvironmentChanged?.Invoke(this, EventArgs.Empty);
        return new EvaluationResult(index, outputs, sw.Elapsed);
    }

    private void Record(int index, Expr value)
    {
        if (value is StrExpr) return;
        _out[index] = value;
        _history.Add(value);
        if (_history.Count > 1000) _history.RemoveAt(0);
    }

    public static CellOutput ToOutput(Expr value) => value switch
    {
        GraphicsExpr g => new CellOutput(OutputKind.Plot, "Graphics", value, g.Spec),
        StrExpr s => new CellOutput(OutputKind.Text, s.Value, value),
        _ => new CellOutput(OutputKind.Expression, TextFormatter.Format(value), value, null, LatexFormatter.Format(value)),
    };

    // ---- evaluation ------------------------------------------------------------------------------------

    public Expr Eval(Expr e)
    {
        Interrupt.Check();
        if (++_depth > 2500) throw new MathException("Recursion limit exceeded.");
        try
        {
            return EvalCore(e);
        }
        finally { _depth--; }
    }

    private Expr EvalCore(Expr e)
    {
        switch (e)
        {
            case Num or Real or StrExpr or GraphicsExpr:
                return e;
            case Sym s:
                if (_shadowed.Contains(s.Name)) return s;
                if (s.Name == "ans") return _history.Count > 0 ? _history[^1] : s;
                return _vars.TryGetValue(s.Name, out var v) ? v : s;
            case FnExpr f when f.Name.StartsWith('@'):
                return EvalSpecial(f);
            case FnExpr f:
                return f.WithArgs(f.Args.Select(Eval).ToArray());
            default:
                // Canonical values (e.g. from history): substitute current variable values.
                return e.Args.Count == 0 ? e : e.WithArgs(e.Args.Select(Eval).ToArray());
        }
    }

    private Expr EvalSpecial(FnExpr f)
    {
        var a = f.Args;
        switch (f.Name)
        {
            case "@plus": return Eval(a[0]) + Eval(a[1]);
            case "@minus": return Eval(a[0]) - Eval(a[1]);
            case "@neg": return -Eval(a[0]);
            case "@times": return Multiply(Eval(a[0]), Eval(a[1]));
            case "@etimes": return Eval(a[0]) * Eval(a[1]);
            case "@dot": return LinearAlgebra.Dot(Eval(a[0]), Eval(a[1]));
            case "@div":
                {
                    var num = Eval(a[0]);
                    var den = Eval(a[1]);
                    if (LinearAlgebra.IsMatrix(den)) return Multiply(num, LinearAlgebra.Inverse(den));
                    if (den.IsZero && !num.IsZero) return Expr.ComplexInfinity;
                    return num / den;
                }
            case "@power":
                {
                    var b = Eval(a[0]);
                    var x = Eval(a[1]);
                    if (LinearAlgebra.IsMatrix(b) && x is Num { Value.IsInteger: true } n)
                        return LinearAlgebra.MatrixPower(b, (int)n.Value.Num);
                    return Expr.Power(b, x);
                }
            case "@eq": return Expr.Equation(Eval(a[0]), Eval(a[1]));
            case "@rel" when a[1] is FnExpr { Name: "@rel" } chain:
                {
                    // a op1 m op2 b holds when both comparisons hold.
                    var inner = EvalSpecial(chain);
                    var middle = inner is FnExpr { Name: "@rel" } ir ? ir.Args[2] : Eval(chain.Args[2]);
                    var outer = Relation(((StrExpr)a[0]).Value, middle, Eval(a[2]));
                    if (inner is Sym { Name: "False" } || outer is Sym { Name: "False" }) return new Sym("False");
                    if (inner is Sym { Name: "True" }) return outer;
                    if (outer is Sym { Name: "True" }) return inner;
                    return FnExpr.Inert("@rel", a[0], inner, ((FnExpr)outer).Args[2]);
                }
            case "@rel": return Relation(((StrExpr)a[0]).Value, Eval(a[1]), Eval(a[2]));
            case "@list": return Expr.List(a.Select(Eval));
            case "@index": return Index(Eval(a[0]), a.Skip(1).Select(Eval).ToList());
            case "@out": return Out(a[0]);
            case "@set": return Assign(a[0], a[1]);
            case "@call": return Call(a[0], a.Skip(1).ToList());
            case "@prime":
                // A bare y' (as used in differential equations) stays an inert derivative marker.
                return FnExpr.Inert("@d", Eval(a[0]), a[1]);
        }
        throw new MathException($"Unknown operator {f.Name}.");
    }

    /// <summary>MATLAB-style product: matrix·matrix and matrix·vector multiply; everything else is elementwise.</summary>
    private static Expr Multiply(Expr x, Expr y)
    {
        bool xm = LinearAlgebra.IsMatrix(x), ym = LinearAlgebra.IsMatrix(y);
        if ((xm && (ym || LinearAlgebra.IsVector(y))) || (ym && LinearAlgebra.IsVector(x)))
            return LinearAlgebra.MapEntries(LinearAlgebra.Dot(x, y), Expansion.Expand);
        return x * y;
    }

    public static Expr Relation(string op, Expr l, Expr r)
    {
        var d = l - r;
        // Decided by the assumptions (assume(x > 0): x + 1 > 0 is True).
        if (!d.IsConstant && Engine.Assumptions.Sign(d) is int sign)
        {
            bool? decided = op switch
            {
                "<" => sign < 0, ">" => sign > 0, "<=" => sign <= 0, ">=" => sign >= 0,
                "!=" => sign != 0, "==" => sign == 0, _ => null,
            };
            if (decided is bool dv) return new Sym(dv ? "True" : "False");
        }
        if (!d.IsConstant && op is "<=" or ">=")
        {
            var range = Engine.Assumptions.Range(d);
            if (op == ">=" && range.NonNegative) return new Sym("True");
            if (op == "<=" && range.NonPositive) return new Sym("True");
        }
        if (d.IsConstant && !d.IsUndefinedOrInfinite)
        {
            double v;
            try { v = Numerics.NumericEval.EvalReal(d); }
            catch (MathException) { v = double.NaN; }
            if (!double.IsNaN(v))
            {
                bool result = op switch
                {
                    "<" => v < 0,
                    ">" => v > 0,
                    "<=" => v <= 0,
                    ">=" => v >= 0,
                    "!=" => Math.Abs(v) > 1e-15,
                    "==" => Math.Abs(v) <= 1e-15,
                    _ => false,
                };
                return new Sym(result ? "True" : "False");
            }
        }
        if (op == "==") return d.IsZero ? new Sym("True") : Expr.Equation(l, r);
        return FnExpr.Inert("@rel", new StrExpr(op), l, r);
    }

    private static Expr Index(Expr target, List<Expr> idx)
    {
        foreach (var i in idx)
        {
            if (target is not ListExpr l) throw new MathException("Only lists and matrices can be indexed.");
            if (i is not Num { Value.IsInteger: true } n) throw new MathException("Indices must be integers.");
            var k = (int)n.Value.Num;
            if (k < 0) k = l.Items.Count + 1 + k;
            if (k < 1 || k > l.Items.Count) throw new MathException($"Index {n} is out of range 1..{l.Items.Count}.");
            target = l.Items[k - 1];
        }
        return target;
    }

    private Expr Out(Expr arg)
    {
        var n = (int)((Num)arg).Value.Num;
        if (n < 0)
        {
            int k = _history.Count + n;
            if (k < 0) throw new MathException("There is no previous output.");
            return _history[k];
        }
        if (_out.TryGetValue(n, out var v)) return v;
        throw new MathException($"Out[{n}] does not exist.");
    }

    private Expr Assign(Expr lhs, Expr rhs)
    {
        if (lhs is Sym s)
        {
            if (s.IsSpecial || Builtins.IsBuiltin(s.Name)) throw new MathException($"'{s.Name}' is protected and cannot be assigned.");
            var value = Eval(rhs);
            _vars[s.Name] = value;
            _funcs.Remove(s.Name);
            return value;
        }
        if (lhs is FnExpr { Name: "@call" } call && call.Args[0] is Sym fname)
        {
            if (Builtins.IsBuiltin(fname.Name) || Functions.Numeric.Contains(fname.Name))
                throw new MathException($"'{fname.Name}' is a built-in function and cannot be redefined.");
            var pars = call.Args.Skip(1).ToList();
            if (pars.Any(p => p is not Sym)) throw new MathException("Function parameters must be plain names, e.g. f(x, y) := x^2 + y.");
            var names = pars.Cast<Sym>().Select(p => p.Name).ToList();
            _funcs[fname.Name] = new UserFunction(fname.Name, names, rhs);
            _vars.Remove(fname.Name);
            // Show the definition with the body evaluated symbolically (not possible for e.g. recursive definitions).
            try
            {
                var shown = WithShadowed(names, () => Eval(rhs));
                return Expr.Equation(FnExpr.Inert(fname.Name, pars.ToArray()), shown);
            }
            catch (MathException)
            {
                return new StrExpr($"Defined {fname.Name}({string.Join(", ", names)}).");
            }
        }
        throw new MathException("Can only assign to a name (x := 2) or define a function (f(x) := x^2).");
    }

    public T WithShadowed<T>(IEnumerable<string> names, Func<T> body)
    {
        var added = names.Where(n => _shadowed.Add(n)).ToList();
        try { return body(); }
        finally { foreach (var n in added) _shadowed.Remove(n); }
    }

    private Expr Call(Expr head, List<Expr> rawArgs)
    {
        if (head is FnExpr { Name: "@prime" } prime)
            return CallDerivative(prime, rawArgs);
        if (head is not Sym s) throw new MathException("Invalid function call.");
        var name = Builtins.Canonical(s.Name);

        if (_funcs.TryGetValue(s.Name, out var uf))
        {
            var args = rawArgs.Select(Eval).ToList();
            return CallUser(uf, args);
        }

        if (Builtins.TryGet(name, out var builtin))
        {
            if (rawArgs.Count < builtin.MinArgs || rawArgs.Count > builtin.MaxArgs)
                throw new MathException($"{builtin.Name} expects {ArgCount(builtin)}. Usage: {builtin.Usage}");
            if (builtin.Lazy is not null) return builtin.Lazy(this, rawArgs);
            var bound = builtin.BoundArgs.Where(i => i < rawArgs.Count).SelectMany(i => BoundNames(rawArgs[i])).ToList();
            var args = WithShadowed(bound, () => rawArgs.Select(Eval).ToArray());
            return WithShadowed(bound, () => builtin.Handler!(this, args));
        }

        if (_vars.TryGetValue(s.Name, out var val) && rawArgs.Count == 1 && val is not ListExpr)
            throw new MathException($"'{s.Name}' is a variable, not a function. Use {s.Name}*(...) for multiplication.");

        return Expr.Call(name, rawArgs.Select(Eval).ToArray());
    }

    private static string ArgCount(Builtin b) =>
        b.MinArgs == b.MaxArgs ? $"{b.MinArgs} argument{(b.MinArgs == 1 ? "" : "s")}"
        : b.MaxArgs == int.MaxValue ? $"at least {b.MinArgs} arguments" : $"{b.MinArgs} to {b.MaxArgs} arguments";

    private static IEnumerable<string> BoundNames(Expr raw) => raw switch
    {
        Sym s when !s.IsSpecial => [s.Name],
        FnExpr { Name: "@list" } l => l.Args.Take(1).Concat(l.Args.Count > 0 && l.Args.All(i => i is Sym) ? l.Args.Skip(1) : []).OfType<Sym>().Select(x => x.Name),
        FnExpr { Name: "@eq" } q when q.Args[0] is Sym ls => [ls.Name],
        _ => [],
    };

    /// <summary>Evaluates a parsed expression with a variable temporarily bound to a value.</summary>
    public Expr EvalWith(string name, Expr value, Expr raw)
    {
        bool had = _vars.TryGetValue(name, out var old);
        bool wasShadowed = _shadowed.Remove(name);
        _vars[name] = value;
        try { return Eval(raw); }
        finally
        {
            if (had) _vars[name] = old!; else _vars.Remove(name);
            if (wasShadowed) _shadowed.Add(name);
        }
    }

    private Expr CallUser(UserFunction uf, List<Expr> args)
    {
        if (args.Count != uf.Parameters.Count)
            throw new MathException($"{uf.Name} expects {uf.Parameters.Count} argument(s), got {args.Count}.");
        // Bind parameters in a fresh frame.
        var saved = new Dictionary<string, Expr?>();
        var wasShadowed = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var p = uf.Parameters[i];
            saved[p] = _vars.TryGetValue(p, out var old) ? old : null;
            if (_shadowed.Remove(p)) wasShadowed.Add(p);
            _vars[p] = args[i];
        }
        try
        {
            return Eval(uf.Body);
        }
        finally
        {
            foreach (var (p, old) in saved)
            {
                if (old is null) _vars.Remove(p); else _vars[p] = old;
            }
            foreach (var p in wasShadowed) _shadowed.Add(p);
        }
    }

    private Expr CallDerivative(FnExpr prime, List<Expr> rawArgs)
    {
        var order = (int)((Num)prime.Args[1]).Value.Num;
        if (prime.Args[0] is Sym fs && _funcs.TryGetValue(fs.Name, out var uf) && uf.Parameters.Count == 1)
        {
            var p = new Sym(uf.Parameters[0]);
            var body = WithShadowed(uf.Parameters, () => Eval(uf.Body));
            var d = Calculus.Derivative.Diff(body, p, order);
            var arg = Eval(rawArgs[0]);
            return d.Substitute(p, arg);
        }
        if (prime.Args[0] is Sym unknown && rawArgs.Count == 1)
        {
            var arg = Eval(rawArgs[0]);
            return order == 1
                ? FnExpr.Inert("diff", FnExpr.Inert(unknown.Name, arg), arg)
                : FnExpr.Inert("diff", FnExpr.Inert(unknown.Name, arg), arg, order);
        }
        throw new MathException("f' notation needs a one-argument function, e.g. f(x) := x^3 then f'(2).");
    }

    /// <summary>Evaluates text and returns the value of the last statement (for tests and tools).</summary>
    public Expr EvalText(string input)
    {
        var result = Evaluate(input);
        var err = result.Outputs.FirstOrDefault(o => o.Kind == OutputKind.Error);
        if (err is not null) throw new MathException(err.Text);
        return _out.TryGetValue(result.Index, out var v) ? v : result.Outputs.LastOrDefault()?.Value ?? Expr.Undefined;
    }
}
