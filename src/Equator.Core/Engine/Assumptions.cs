using System.Numerics;

namespace Equator.Core.Engine;

/// <summary>Known facts about one symbol: domain flags and an interval (bounds may be open or closed).</summary>
public sealed record SymbolFacts(bool Real, bool Integer, double Lo, bool LoStrict, double Hi, bool HiStrict, bool NonZero)
{
    public static readonly SymbolFacts None = new(false, false, double.NegativeInfinity, true, double.PositiveInfinity, true, false);

    public SymbolFacts Intersect(SymbolFacts o)
    {
        var (lo, loS) = Lo > o.Lo ? (Lo, LoStrict) : o.Lo > Lo ? (o.Lo, o.LoStrict) : (Lo, LoStrict || o.LoStrict);
        var (hi, hiS) = Hi < o.Hi ? (Hi, HiStrict) : o.Hi < Hi ? (o.Hi, o.HiStrict) : (Hi, HiStrict || o.HiStrict);
        return new(Real || o.Real, Integer || o.Integer, lo, loS, hi, hiS, NonZero || o.NonZero);
    }

    public bool IsEmpty => Lo > Hi || (Lo == Hi && (LoStrict || HiStrict));
}

/// <summary>A set of assumptions about symbols (immutable; a kernel holds one, refine/simplify push extended copies).</summary>
public sealed class AssumptionSet
{
    public static readonly AssumptionSet Empty = new(new Dictionary<string, SymbolFacts>());

    private readonly Dictionary<string, SymbolFacts> _facts;
    private AssumptionSet(Dictionary<string, SymbolFacts> facts) { _facts = facts; }

    public bool IsEmpty => _facts.Count == 0;
    public IReadOnlyDictionary<string, SymbolFacts> Facts => _facts;
    public SymbolFacts For(string name) => _facts.TryGetValue(name, out var f) ? f : SymbolFacts.None;

    public AssumptionSet With(string name, SymbolFacts facts)
    {
        var d = new Dictionary<string, SymbolFacts>(_facts);
        var merged = d.TryGetValue(name, out var old) ? old.Intersect(facts) : facts;
        if (merged.IsEmpty) throw new MathException(Messages.T("assume: the assumptions about {0} contradict each other.", name));
        d[name] = merged;
        return new(d);
    }

    public AssumptionSet Without(string name)
    {
        var d = new Dictionary<string, SymbolFacts>(_facts);
        d.Remove(name);
        return new(d);
    }

    /// <summary>Adds a condition: a relation between a symbol and a real constant (chains allowed), or a property name.</summary>
    public AssumptionSet Add(Expr condition)
    {
        switch (condition)
        {
            case FnExpr { Name: "@rel" } rel when rel.Args[1] is FnExpr { Name: "@rel" } inner:
                // a < x < b
                return Add(inner).Add(FnExpr.Inert("@rel", rel.Args[0], inner.Args[2], rel.Args[2]));
            case FnExpr { Name: "@rel" } rel:
                {
                    var op = ((StrExpr)rel.Args[0]).Value;
                    var (l, r) = (rel.Args[1], rel.Args[2]);
                    if (l is Sym ls && !ls.IsSpecial && Constant(r) is { } rv) return With(ls.Name, FromRelation(op, rv));
                    if (r is Sym rs && !rs.IsSpecial && Constant(l) is { } lv) return With(rs.Name, FromRelation(Flip(op), lv));
                    throw new MathException(Messages.T("assume: a condition must compare a variable with a number, e.g. x > 0."));
                }
            case Sym { Name: "True" }:
                return this;
        }
        throw new MathException(Messages.T("assume: expected a condition such as x > 0, or a property such as assume(x, real)."));
    }

    public AssumptionSet AddProperty(Sym x, string property)
    {
        var f = property switch
        {
            "real" => SymbolFacts.None with { Real = true },
            "integer" => SymbolFacts.None with { Real = true, Integer = true },
            "positive" => SymbolFacts.None with { Real = true, Lo = 0, LoStrict = true },
            "negative" => SymbolFacts.None with { Real = true, Hi = 0, HiStrict = true },
            "nonnegative" => SymbolFacts.None with { Real = true, Lo = 0, LoStrict = false },
            "nonpositive" => SymbolFacts.None with { Real = true, Hi = 0, HiStrict = false },
            "nonzero" => SymbolFacts.None with { NonZero = true },
            "natural" => SymbolFacts.None with { Real = true, Integer = true, Lo = 0, LoStrict = false },
            "complex" => SymbolFacts.None,
            _ => throw new MathException(Messages.T("assume: unknown property '{0}'. Use real, integer, positive, negative, nonnegative, nonpositive, nonzero or natural.", property)),
        };
        return With(x.Name, f);
    }

    private static double? Constant(Expr e)
    {
        if (e == Expr.Infinity) return double.PositiveInfinity;
        if (e.IsInfinite && e.HasNegativeCoefficient) return double.NegativeInfinity;
        if (!e.IsConstant || e.IsUndefinedOrInfinite) return null;
        var v = Numerics.NumericEval.EvalReal(e);
        return double.IsNaN(v) ? null : v;
    }

    private static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    private static SymbolFacts FromRelation(string op, double v) => op switch
    {
        ">" => SymbolFacts.None with { Real = true, Lo = v, LoStrict = true, NonZero = v >= 0 },
        ">=" => SymbolFacts.None with { Real = true, Lo = v, LoStrict = false, NonZero = v > 0 },
        "<" => SymbolFacts.None with { Real = true, Hi = v, HiStrict = true, NonZero = v <= 0 },
        "<=" => SymbolFacts.None with { Real = true, Hi = v, HiStrict = false, NonZero = v < 0 },
        "!=" when v == 0 => SymbolFacts.None with { NonZero = true },
        "==" => SymbolFacts.None with { Real = true, Lo = v, Hi = v, LoStrict = false, HiStrict = false },
        _ => throw new MathException(Messages.T("assume: unsupported relation {0}.", op)),
    };

    /// <summary>Human-readable list of the assumptions.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var (name, f) in _facts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var parts = new List<string>();
            if (f.Integer) parts.Add($"{name} ∈ ℤ");
            else if (f.Real) parts.Add($"{name} ∈ ℝ");
            if (!double.IsNegativeInfinity(f.Lo) && !double.IsPositiveInfinity(f.Hi) && f.Lo == f.Hi) parts.Add($"{name} = {f.Lo}");
            else
            {
                if (!double.IsNegativeInfinity(f.Lo)) parts.Add($"{name} {(f.LoStrict ? ">" : "≥")} {Fmt(f.Lo)}");
                if (!double.IsPositiveInfinity(f.Hi)) parts.Add($"{name} {(f.HiStrict ? "<" : "≤")} {Fmt(f.Hi)}");
            }
            if (f.NonZero && (f.Lo <= 0 && f.Hi >= 0)) parts.Add($"{name} ≠ 0");
            yield return string.Join(", ", parts);
        }
        static string Fmt(double v) => v.ToString("G15", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Real interval with open or closed ends; Real is false when the value may be non-real.</summary>
public readonly record struct Interval(double Lo, bool LoOpen, double Hi, bool HiOpen, bool Real)
{
    public static readonly Interval Unknown = new(double.NegativeInfinity, true, double.PositiveInfinity, true, false);
    public static readonly Interval AnyReal = new(double.NegativeInfinity, true, double.PositiveInfinity, true, true);
    public static Interval Point(double v) => new(v, false, v, false, true);

    public bool Positive => Real && (Lo > 0 || (Lo == 0 && LoOpen));
    public bool Negative => Real && (Hi < 0 || (Hi == 0 && HiOpen));
    public bool NonNegative => Real && Lo >= 0;
    public bool NonPositive => Real && Hi <= 0;
    public bool ContainsZero => !(Positive || Negative);
}

/// <summary>
/// The assumptions in effect (ambient, per thread) and inference of sign, realness and integrality of
/// expressions by interval arithmetic. With no assumptions every query is answered from the expression
/// structure alone (numbers, π, e, exp, squares of real things, ...).
/// </summary>
public static class Assumptions
{
    [ThreadStatic] private static AssumptionSet? _current;

    public static AssumptionSet Current => _current ?? AssumptionSet.Empty;

    public static IDisposable Scope(AssumptionSet set)
    {
        var previous = _current;
        _current = set;
        return new Restore(previous);
    }

    private sealed class Restore(AssumptionSet? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    /// <summary>
    /// Runs body with an extra property for a bound variable: integration and limit variables range over the
    /// reals, summation indices over the integers. Existing (stronger) assumptions are kept.
    /// </summary>
    public static T WithVariable<T>(Expr variable, string property, Func<T> body)
    {
        if (variable is not Sym s || s.IsSpecial) return body();
        AssumptionSet set;
        try { set = Current.AddProperty(s, property); }
        catch (MathException) { return body(); }
        using (Scope(set)) return body();
    }

    /// <summary>Rebuilds an expression bottom-up so the canonical constructors apply the assumptions in effect.</summary>
    public static Expr Refine(Expr e)
    {
        if (!Active || e.Args.Count == 0) return e;
        var args = new Expr[e.Args.Count];
        bool changed = false;
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = Refine(e.Args[i]);
            changed |= !ReferenceEquals(args[i], e.Args[i]);
        }
        // Inert nodes (unevaluated integrals, relations) keep their form; everything else re-canonicalises.
        if (e is FnExpr f && f.Name.StartsWith('@')) return changed ? FnExpr.Inert(f.Name, args) : e;
        return e.WithArgs(args);
    }

    /// <summary>Replaces the assumptions of the current scope (used by assume/forget during an evaluation).</summary>
    internal static void SetCurrent(AssumptionSet set) => _current = set;

    /// <summary>True when there is at least one assumption, i.e. refinements beyond pure structure may apply.</summary>
    public static bool Active => _current is { IsEmpty: false };

    /// <summary>A numeric value for s near v consistent with its assumptions (used for sample-point checks).</summary>
    public static Complex SampleValue(Sym s, double v)
    {
        var f = Current.For(s.Name);
        if (f.Integer) v = Math.Round(v * 3) + 1;
        double lo = f.Lo, hi = f.Hi;
        if (!double.IsInfinity(lo) && !double.IsInfinity(hi))
        {
            // Map into the interval.
            double t = (Math.Abs(v) % 1.0 + 0.1) / 1.2;
            var r = lo + (hi - lo) * t;
            return f.Integer ? Math.Round(r) : r;
        }
        if (!double.IsInfinity(lo)) return lo + Math.Abs(v) + (f.LoStrict || f.Integer ? 0.5 : 0) + (f.Integer ? 0.5 : 0);
        if (!double.IsInfinity(hi)) return hi - Math.Abs(v) - (f.HiStrict || f.Integer ? 0.5 : 0) - (f.Integer ? 0.5 : 0);
        return v;
    }

    // ---- queries ------------------------------------------------------------------------------------

    public static bool IsReal(Expr e) => Range(e).Real;
    public static bool IsPositive(Expr e) => Range(e).Positive;
    public static bool IsNegative(Expr e) => Range(e).Negative;
    public static bool IsNonNegative(Expr e) => Range(e).NonNegative;
    public static bool IsNonPositive(Expr e) => Range(e).NonPositive;

    public static bool IsNonZero(Expr e)
    {
        if (e is Sym s && Current.For(s.Name).NonZero) return true;
        var r = Range(e);
        return r.Positive || r.Negative;
    }

    /// <summary>+1, −1, 0 (exactly zero) or null when unknown.</summary>
    public static int? Sign(Expr e)
    {
        if (e.IsZero) return 0;
        var r = Range(e);
        if (r.Positive) return 1;
        if (r.Negative) return -1;
        return null;
    }

    public static bool IsInteger(Expr e)
    {
        switch (e)
        {
            case Num n: return n.Value.IsInteger;
            case Sym s: return Current.For(s.Name).Integer;
            case AddExpr a: return a.Terms.All(IsInteger);
            case MulExpr m: return m.Factors.All(IsInteger);
            case PowExpr { Exponent: Num { Value.IsInteger: true, Value.Sign: >= 0 } } p: return IsInteger(p.Base);
            case PowExpr p when IsInteger(p.Exponent) && IsNonNegative(p.Exponent) && IsInteger(p.Base): return true;
            case FnExpr { Name: "floor" or "ceil" or "round" } f: return IsReal(f.Args[0]);
            case FnExpr { Name: "factorial" } f: return IsInteger(f.Args[0]) && IsNonNegative(f.Args[0]);
            case FnExpr { Name: "binomial" } f: return f.Args.All(IsInteger);
        }
        return false;
    }

    /// <summary>An enclosing interval for the value of e (real only when e is known to be real).</summary>
    public static Interval Range(Expr e)
    {
        switch (e)
        {
            case Num n:
                return Interval.Point(n.Value.ToDouble());
            case Real r:
                return Interval.Point(r.Value);
            case Sym s:
                switch (s.Name)
                {
                    case "Pi": return Interval.Point(Math.PI);
                    case "E": return Interval.Point(Math.E);
                    case "I": case "ComplexInfinity": case "Undefined": return Interval.Unknown;
                    case "Infinity": return new(double.PositiveInfinity, false, double.PositiveInfinity, false, true);
                }
                {
                    var f = Current.For(s.Name);
                    if (!f.Real) return Interval.Unknown;
                    return new(f.Lo, f.LoStrict, f.Hi, f.HiStrict, true);
                }
            case AddExpr a:
                {
                    var acc = Interval.Point(0);
                    foreach (var t in a.Terms)
                    {
                        acc = Add(acc, Range(t));
                        if (!acc.Real) return Interval.Unknown;
                    }
                    return acc;
                }
            case MulExpr m:
                {
                    var acc = Interval.Point(1);
                    foreach (var f in m.Factors)
                    {
                        acc = Mul(acc, Range(f));
                        if (!acc.Real) return Interval.Unknown;
                    }
                    return acc;
                }
            case PowExpr p:
                return PowRange(p);
            case FnExpr f when f.Args.Count == 1:
                return FunctionRange(f.Name, f.Args[0]);
        }
        return Interval.Unknown;
    }

    private static Interval Add(Interval a, Interval b)
    {
        if (!a.Real || !b.Real) return Interval.Unknown;
        return new(a.Lo + b.Lo, a.LoOpen || b.LoOpen, a.Hi + b.Hi, a.HiOpen || b.HiOpen, true);
    }

    private static Interval Mul(Interval a, Interval b)
    {
        if (!a.Real || !b.Real) return Interval.Unknown;
        (double V, bool Open) P(double x, bool xo, double y, bool yo)
        {
            if (x == 0 && !xo || y == 0 && !yo) return (0, false);
            double v = x * y;
            if (double.IsNaN(v)) v = 0; // 0 · ∞ at an open end
            return (v, xo || yo);
        }
        var c = new[] { P(a.Lo, a.LoOpen, b.Lo, b.LoOpen), P(a.Lo, a.LoOpen, b.Hi, b.HiOpen), P(a.Hi, a.HiOpen, b.Lo, b.LoOpen), P(a.Hi, a.HiOpen, b.Hi, b.HiOpen) };
        var lo = c.OrderBy(t => t.V).ThenBy(t => t.Open).First();
        var hi = c.OrderByDescending(t => t.V).ThenBy(t => t.Open).First();
        return new(lo.V, lo.Open, hi.V, hi.Open, true);
    }

    private static Interval PowRange(PowExpr p)
    {
        if (p.Base == Expr.E)
        {
            var x = Range(p.Exponent);
            if (!x.Real) return Interval.Unknown;
            return new Interval(Math.Exp(x.Lo), true, Math.Exp(x.Hi), x.HiOpen, true);
        }
        var b = Range(p.Base);
        if (p.Exponent is Num { Value: var q })
        {
            if (q.IsInteger)
            {
                int n = (int)BigInteger.Clamp(q.Num, -1000, 1000);
                if (!b.Real) return Interval.Unknown;
                if (n > 0 && n % 2 == 0)
                {
                    // Even power: |b|^n.
                    double aLo = Math.Abs(b.Lo), aHi = Math.Abs(b.Hi);
                    double hi = Math.Pow(Math.Max(aLo, aHi), n);
                    bool hiOpen = aLo > aHi ? b.LoOpen : aHi > aLo ? b.HiOpen : b.LoOpen && b.HiOpen;
                    if (b.ContainsZero) return new(0, IsNonZero(p.Base), hi, hiOpen, true);
                    double lo = Math.Pow(Math.Min(aLo, aHi), n);
                    bool loOpen = aLo < aHi ? b.LoOpen : b.HiOpen;
                    return new(lo, loOpen, hi, hiOpen, true);
                }
                if (n > 0) return new(Math.Pow(b.Lo, n), b.LoOpen, Math.Pow(b.Hi, n), b.HiOpen, true);
                if (n < 0)
                {
                    if (b.Positive) return new(Math.Pow(b.Hi, n), b.HiOpen || double.IsInfinity(b.Hi), b.Lo == 0 ? double.PositiveInfinity : Math.Pow(b.Lo, n), b.LoOpen, true);
                    if (b.Negative && n % 2 == 0) return new(Math.Pow(b.Lo, n), b.LoOpen, b.Hi == 0 ? double.PositiveInfinity : Math.Pow(b.Hi, n), true, true);
                    if (b.Negative) return new(b.Hi == 0 ? double.NegativeInfinity : Math.Pow(b.Hi, n), true, Math.Pow(b.Lo, n), b.LoOpen, true);
                    return b.Real && IsNonZero(p.Base) ? Interval.AnyReal : Interval.Unknown;
                }
                return Interval.Point(1);
            }
            // Fractional power of a non-negative base is real and monotonic.
            if (b.NonNegative)
            {
                double r = q.ToDouble();
                return r > 0
                    ? new Interval(Math.Pow(b.Lo, r), b.LoOpen, Math.Pow(b.Hi, r), b.HiOpen, true)
                    : new(Math.Pow(b.Hi, r), b.HiOpen, b.Lo == 0 ? double.PositiveInfinity : Math.Pow(b.Lo, r), b.LoOpen, true);
            }
            return Interval.Unknown;
        }
        // Positive base, real exponent: positive.
        if (b.Positive && Range(p.Exponent).Real) return new(0, true, double.PositiveInfinity, true, true);
        return Interval.Unknown;
    }

    private static Interval FunctionRange(string name, Expr arg)
    {
        var x = Range(arg);
        switch (name)
        {
            case "exp": return x.Real ? new Interval(Math.Exp(x.Lo), true, Math.Exp(x.Hi), x.HiOpen, true) : Interval.Unknown;
            case "log":
                if (!x.Positive) return Interval.Unknown;
                return new(x.Lo <= 0 ? double.NegativeInfinity : Math.Log(x.Lo), x.LoOpen || x.Lo <= 0, Math.Log(x.Hi), x.HiOpen, true);
            case "abs":
                {
                    if (!x.Real) return new(0, IsNonZero(arg), double.PositiveInfinity, true, true);
                    double aLo = Math.Abs(x.Lo), aHi = Math.Abs(x.Hi);
                    double lo = x.ContainsZero ? 0 : Math.Min(aLo, aHi);
                    return new(lo, !x.ContainsZero, Math.Max(aLo, aHi), true, true);
                }
            case "sin": case "cos": return x.Real ? new Interval(-1, false, 1, false, true) : Interval.Unknown;
            case "tanh": return x.Real ? new Interval(Math.Tanh(x.Lo), x.LoOpen || double.IsInfinity(x.Lo), Math.Tanh(x.Hi), x.HiOpen || double.IsInfinity(x.Hi), true) : Interval.Unknown;
            case "erf": return x.Real ? new Interval(-1, true, 1, true, true) : Interval.Unknown;
            case "atan": return x.Real ? new Interval(Math.Atan(x.Lo), x.LoOpen, Math.Atan(x.Hi), x.HiOpen, true) : Interval.Unknown;
            case "sinh": return x.Real ? new Interval(Math.Sinh(x.Lo), x.LoOpen, Math.Sinh(x.Hi), x.HiOpen, true) : Interval.Unknown;
            case "asinh": return x.Real ? new Interval(Math.Asinh(x.Lo), x.LoOpen, Math.Asinh(x.Hi), x.HiOpen, true) : Interval.Unknown;
            case "cosh": return x.Real ? new Interval(1, false, double.PositiveInfinity, true, true) : Interval.Unknown;
            case "acosh": return x.Real && x.Lo >= 1 ? new Interval(Math.Acosh(x.Lo), x.LoOpen, Math.Acosh(x.Hi), x.HiOpen, true) : Interval.Unknown;
            case "sign": return x.Real ? new Interval(x.Positive ? 1 : -1, false, x.Negative ? -1 : 1, false, true) : Interval.Unknown;
            case "floor": case "ceil": case "round": return x.Real ? new Interval(Math.Floor(x.Lo) - 1, false, Math.Ceiling(x.Hi) + 1, false, true) : Interval.Unknown;
            case "factorial": return x.Real && x.Lo > -1 ? new Interval(0, true, double.PositiveInfinity, true, true) : Interval.Unknown;
            case "gamma": return x.Positive ? new Interval(0, true, double.PositiveInfinity, true, true) : Interval.Unknown;
            case "sqrt": return x.NonNegative ? new Interval(Math.Sqrt(x.Lo), x.LoOpen, Math.Sqrt(x.Hi), x.HiOpen, true) : Interval.Unknown;
        }
        return Interval.Unknown;
    }
}
