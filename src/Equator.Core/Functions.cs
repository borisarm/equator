using System.Numerics;
using Equator.Core.Numerics;

namespace Equator.Core;

/// <summary>Automatic simplification rules for the built-in mathematical functions.</summary>
public static class Functions
{
    /// <summary>Functions with a known numeric implementation (see <see cref="NumericEval"/>).</summary>
    public static readonly HashSet<string> Numeric =
    [
        "sin", "cos", "tan", "cot", "sec", "csc", "asin", "acos", "atan", "acot", "asec", "acsc",
        "sinh", "cosh", "tanh", "coth", "sech", "csch", "asinh", "acosh", "atanh",
        "log", "abs", "sign", "floor", "ceil", "round", "gamma", "factorial", "erf", "re", "im", "conj", "arg", "zeta",
    ];

    private static readonly HashSet<string> OddFunctions = ["sin", "tan", "cot", "csc", "asin", "atan", "acot", "sinh", "tanh", "coth", "csch", "asinh", "atanh", "erf"];
    private static readonly HashSet<string> EvenFunctions = ["cos", "sec", "cosh", "sech", "abs"];

    public static Expr Apply(string name, Expr[] args)
    {
        switch (name)
        {
            case "exp" when args.Length == 1: return Expr.Exp(args[0]);
            case "sqrt" when args.Length == 1: return Expr.Sqrt(args[0]);
            case "root" when args.Length == 2: return Expr.Power(args[0], Expr.One / args[1]);
            case "log" when args.Length == 2: return Apply("log", [args[0]]) / Apply("log", [args[1]]);
            case "log10" when args.Length == 1: return Apply("log", [args[0]]) / Apply("log", [10]);
            case "log2" when args.Length == 1: return Apply("log", [args[0]]) / Apply("log", [2]);
        }

        if (args.Length == 1)
        {
            var x = args[0];
            if (x == Expr.Undefined) return Expr.Undefined;
            if (x is ListExpr list) return Expr.List(list.Items.Select(i => Apply(name, [i])));

            // Floating point argument: evaluate numerically.
            if (Numeric.Contains(name) && x.IsConstant && x.ContainsAny(e => e is Real) && !x.IsUndefinedOrInfinite)
            {
                if (BigEval.PrecisionOf(x) is int digits)
                {
                    try
                    {
                        int bits = BigFloat.DigitsToBits(digits) + 16;
                        return BigEval.ToExpr(BigEval.Function(name, [BigEval.Eval(x, bits)], bits), digits);
                    }
                    catch (MathException) { return FnExpr.Inert(name, x); }
                    catch (DivideByZeroException) { return Expr.ComplexInfinity; }
                }
                var v = NumericEval.Eval(FnExpr.Inert(name, x));
                if (!double.IsNaN(v.Real)) return NumericEval.FromComplex(v);
            }

            var special = Special(name, x);
            if (special is not null) return special;

            if (OddFunctions.Contains(name) && x.HasNegativeCoefficient)
                return -Apply(name, [-x]);
            if (EvenFunctions.Contains(name) && x.HasNegativeCoefficient)
                return Apply(name, [-x]);
        }
        else if (args.Length == 2)
        {
            var special = Special2(name, args[0], args[1]);
            if (special is not null) return special;
        }

        if (name is "max" or "min" && args.Length > 0 && args.All(a => a.IsNumber || (a.IsConstant && !a.Contains(Expr.I))))
        {
            var vals = args.Select(NumericEval.EvalReal).ToArray();
            int idx = name == "max" ? Array.IndexOf(vals, vals.Max()) : Array.IndexOf(vals, vals.Min());
            return args[idx];
        }

        return FnExpr.Inert(name, args);
    }

    private static Expr? Special(string name, Expr x)
    {
        switch (name)
        {
            case "sin": return TrigExact(x, 0);
            case "cos": return TrigExact(x, 1);
            case "tan":
                {
                    if (x is FnExpr { Name: "atan" } at) return at.Args[0];
                    foreach (var (arg, value) in AtanTable)
                    {
                        if (x == value) return arg;
                        if (x == -value) return -arg;
                    }
                    var s = TrigExact(x, 0); var c = TrigExact(x, 1);
                    if (s is not null && c is not null) return c.IsZero ? Expr.ComplexInfinity : s / c;
                    return null;
                }
            case "cot":
                {
                    var s = TrigExact(x, 0); var c = TrigExact(x, 1);
                    if (s is not null && c is not null) return s.IsZero ? Expr.ComplexInfinity : c / s;
                    return null;
                }
            case "sec":
                {
                    var c = TrigExact(x, 1);
                    return c is null ? null : c.IsZero ? Expr.ComplexInfinity : Expr.One / c;
                }
            case "csc":
                {
                    var s = TrigExact(x, 0);
                    return s is null ? null : s.IsZero ? Expr.ComplexInfinity : Expr.One / s;
                }
            case "asin":
                if (x is FnExpr { Name: "sin" }) return null;
                return InverseTable(x, AsinTable);
            case "acos":
                {
                    var a = InverseTable(x, AsinTable);
                    return a is null ? null : Expr.Pi / 2 - a;
                }
            case "atan":
                if (x == Expr.Infinity) return Expr.Pi / 2;
                if (x.IsInfinite && x.HasNegativeCoefficient) return -Expr.Pi / 2;
                return InverseTable(x, AtanTable);
            case "acot":
                {
                    if (x.IsZero) return Expr.Pi / 2;
                    var a = InverseTable(x, AtanTable);
                    return a is null ? null : Expr.Pi / 2 - a;
                }
            case "sinh": case "tanh": case "asinh": case "atanh": case "erf":
                if (x.IsZero) return Expr.Zero;
                if (name == "erf" && x == Expr.Infinity) return Expr.One;
                if (name == "tanh" && x == Expr.Infinity) return Expr.One;
                return null;
            case "cosh": case "sech":
                return x.IsZero ? Expr.One : null;
            case "acosh":
                return x.IsOne ? Expr.Zero : null;
            case "log":
                if (x.IsOne) return Expr.Zero;
                if (x == Expr.E) return Expr.One;
                if (x.IsZero) return -Expr.Infinity;
                if (x == Expr.Infinity) return Expr.Infinity;
                if (x is PowExpr { Base: Sym { Name: "E" } } pe) return pe.Exponent;
                if (x == Expr.I) return Expr.I * Expr.Pi / 2;
                if (x is Num n && n.Value.IsMinusOne) return Expr.I * Expr.Pi;
                if (x is Num r && r.Value.Sign > 0 && !r.Value.IsInteger && r.Value.Num.IsOne)
                    return -Apply("log", [new Num(r.Value.Den)]);
                return null;
            case "abs":
                if (x.IsNumber) return x is Num n2 ? new Num(n2.Value.Abs()) : new Real(Math.Abs(((Real)x).Value));
                if (x is Sym { IsNumericConstant: true } && x != Expr.I) return x;
                if (x == Expr.I) return Expr.One;
                if (x.IsConstant && !x.Contains(Expr.I) && !x.IsUndefinedOrInfinite)
                {
                    var v = NumericEval.EvalReal(x);
                    if (v > 0) return x;
                    if (v < 0) return -x;
                }
                if (x is FnExpr { Name: "abs" }) return x;
                if (x is PowExpr { Exponent: Num { Value.IsInteger: true } ev } pa && ev.Value.Num.IsEven) return x;
                return null;
            case "sign":
                if (x.IsConstant && !x.Contains(Expr.I) && !x.IsUndefinedOrInfinite)
                    return Math.Sign(NumericEval.EvalReal(x));
                return null;
            case "floor": case "ceil": case "round":
                if (x is Num q)
                {
                    return name switch
                    {
                        "floor" => new Num(q.Value.Floor()),
                        "ceil" => new Num(q.Value.Ceiling()),
                        _ => new Num((q.Value + BigRational.Half).Floor()),
                    };
                }
                if (x.IsConstant && !x.Contains(Expr.I) && !x.IsUndefinedOrInfinite)
                {
                    var v = NumericEval.EvalReal(x);
                    double res = name == "floor" ? Math.Floor(v) : name == "ceil" ? Math.Ceiling(v) : Math.Floor(v + 0.5);
                    return new Num(new BigInteger(res));
                }
                return null;
            case "factorial":
                if (x is Num { Value.IsInteger: true } k)
                {
                    if (k.Value.Sign < 0) return Expr.ComplexInfinity;
                    if (k.Value.Num <= 5000) return new Num(IntMath.Factorial((int)k.Value.Num));
                }
                if (x is Num { Value.Den: var den } h && den == 2)
                    return Apply("gamma", [x + 1]);
                return null;
            case "gamma":
                if (x is Num { Value.IsInteger: true } g)
                {
                    if (g.Value.Sign <= 0) return Expr.ComplexInfinity;
                    if (g.Value.Num <= 5001) return new Num(IntMath.Factorial((int)g.Value.Num - 1));
                }
                if (x is Num hg && hg.Value.Den == 2)
                {
                    // gamma(n + 1/2) = (2n)! / (4^n n!) sqrt(pi)
                    var hn = (hg.Value - BigRational.Half).Floor();
                    if (hn >= 0 && hn < 2000)
                    {
                        int ni = (int)hn;
                        var c = new BigRational(IntMath.Factorial(2 * ni), BigInteger.Pow(4, ni) * IntMath.Factorial(ni));
                        return new Num(c) * Expr.Sqrt(Expr.Pi);
                    }
                }
                return null;
            case "zeta":
                if (x is Num { Value.IsInteger: true } zn && System.Numerics.BigInteger.Abs(zn.Value.Num) <= 200)
                {
                    int s = (int)zn.Value.Num;
                    if (s == 1) return Expr.ComplexInfinity;
                    if (s == 0) return new Num(new BigRational(-1, 2));
                    if (s < 0) return new Num(-Calculus.Sums.Bernoulli(1 - s) / (1 - s)); // ζ(−n) = −B_{n+1}/(n+1)
                    if (s % 2 == 0) return Calculus.Sums.Zeta(s);
                }
                if (x == Expr.Infinity) return Expr.One;
                return null;
            case "re":
                return x.IsNumber ? x : null;
            case "im":
                return x.IsNumber ? Expr.Zero : null;
            case "conj":
                if (x.IsNumber) return x;
                if (x.IsConstant) return x.Substitute(Expr.I, -Expr.I);
                return null;
        }
        return null;
    }

    private static Expr? Special2(string name, Expr a, Expr b)
    {
        switch (name)
        {
            case "binomial":
                if (a is Num { Value.IsInteger: true } n && b is Num { Value.IsInteger: true } k)
                    return new Num(IntMath.Binomial(n.Value.Num, k.Value.Num));
                return null;
            case "atan2":
                if (a.IsZero && b.IsZero) return Expr.Undefined;
                if (a.IsNumber && b.IsNumber || (a.IsConstant && b.IsConstant && !a.Contains(Expr.I) && !b.Contains(Expr.I)))
                {
                    double y = NumericEval.EvalReal(a), xv = NumericEval.EvalReal(b);
                    if (xv > 0) return Apply("atan", [a / b]);
                    if (xv == 0) return y > 0 ? Expr.Pi / 2 : -Expr.Pi / 2;
                    return Apply("atan", [a / b]) + (y >= 0 ? Expr.Pi : -Expr.Pi);
                }
                return null;
            case "mod":
                if (a is Num ma && b is Num mb && !mb.Value.IsZero)
                {
                    var qf = (ma.Value / mb.Value).Floor();
                    return new Num(ma.Value - mb.Value * qf);
                }
                return null;
            case "gcd":
            case "lcm":
                if (a is Num { Value.IsInteger: true } ga && b is Num { Value.IsInteger: true } gb)
                {
                    var g = BigInteger.GreatestCommonDivisor(ga.Value.Num, gb.Value.Num);
                    if (name == "gcd") return new Num(g);
                    return g.IsZero ? Expr.Zero : new Num(BigInteger.Abs(ga.Value.Num * gb.Value.Num) / g);
                }
                // Polynomials (in any generators).
                if (!a.IsNumber || !b.IsNumber)
                {
                    if (a.IsUndefinedOrInfinite || b.IsUndefinedOrInfinite || a.ContainsAny(z => z is Real) || b.ContainsAny(z => z is Real)) return null;
                    var (_, da) = Algebra.Rational.NumDen(a);
                    var (_, db) = Algebra.Rational.NumDen(b);
                    if (!da.IsNumber || !db.IsNumber) return null;
                    return Algebra.Factoring.PolynomialGcd([a, b], lcm: name == "lcm");
                }
                return null;
        }
        return null;
    }

    // ---- exact trigonometric values -------------------------------------------------------------

    /// <summary>sin(x) (kind=0) or cos(x) (kind=1) when x is a rational multiple of pi with a known value.</summary>
    private static Expr? TrigExact(Expr x, int kind)
    {
        if (x.IsZero) return kind == 0 ? Expr.Zero : Expr.One;
        if (x is FnExpr f && f.Args.Count == 1)
        {
            if (kind == 0 && f.Name == "asin") return f.Args[0];
            if (kind == 1 && f.Name == "acos") return f.Args[0];
            if (kind == 1 && f.Name == "asin") return Expr.Sqrt(1 - Expr.Power(f.Args[0], 2));
            if (kind == 0 && f.Name == "acos") return Expr.Sqrt(1 - Expr.Power(f.Args[0], 2));
            if (f.Name == "atan")
            {
                var t = f.Args[0];
                var r = Expr.Sqrt(1 + Expr.Power(t, 2));
                return kind == 0 ? t / r : Expr.One / r;
            }
        }
        BigRational c;
        if (x == Expr.Pi) c = BigRational.One;
        else if (x is MulExpr { Factors.Count: 2 } m && m.Factors[0] is Num n && m.Factors[1] == Expr.Pi) c = n.Value;
        else return null;

        // cos(c pi) = sin((c + 1/2) pi)
        if (kind == 1) c += BigRational.Half;
        // reduce to [0, 2)
        var two = new BigRational(2);
        c -= two * (c / two).Floor();
        int sign = 1;
        if (c >= BigRational.One) { c -= BigRational.One; sign = -1; }
        if (c > BigRational.Half) c = BigRational.One - c;
        Expr? v = null;
        if (c.IsZero) v = Expr.Zero;
        else if (c == new BigRational(1, 6)) v = Expr.Half;
        else if (c == new BigRational(1, 4)) v = Expr.Sqrt(2) / 2;
        else if (c == new BigRational(1, 3)) v = Expr.Sqrt(3) / 2;
        else if (c == BigRational.Half) v = Expr.One;
        else if (c == new BigRational(1, 12)) v = (Expr.Sqrt(6) - Expr.Sqrt(2)) / 4;
        else if (c == new BigRational(5, 12)) v = (Expr.Sqrt(6) + Expr.Sqrt(2)) / 4;
        else if (c == new BigRational(1, 8)) v = Expr.Sqrt(2 - Expr.Sqrt(2)) / 2;
        else if (c == new BigRational(3, 8)) v = Expr.Sqrt(2 + Expr.Sqrt(2)) / 2;
        else if (c == new BigRational(1, 10)) v = (Expr.Sqrt(5) - 1) / 4;
        else if (c == new BigRational(3, 10)) v = (Expr.Sqrt(5) + 1) / 4;
        else if (c == new BigRational(1, 5)) v = Expr.Sqrt(10 - 2 * Expr.Sqrt(5)) / 4;
        else if (c == new BigRational(2, 5)) v = Expr.Sqrt(10 + 2 * Expr.Sqrt(5)) / 4;
        if (v is null) return null;
        return sign < 0 ? -v : v;
    }

    private static readonly (Expr Arg, Expr Value)[] AsinTable =
    [
        (Expr.Zero, Expr.Zero),
        (Expr.Half, Expr.Pi / 6),
        (Expr.Sqrt(2) / 2, Expr.Pi / 4),
        (Expr.Sqrt(3) / 2, Expr.Pi / 3),
        (Expr.One, Expr.Pi / 2),
    ];

    private static readonly (Expr Arg, Expr Value)[] AtanTable =
    [
        (Expr.Zero, Expr.Zero),
        (Expr.Sqrt(3) / 3, Expr.Pi / 6),
        (Expr.One, Expr.Pi / 4),
        (Expr.Sqrt(3), Expr.Pi / 3),
        (Expr.Sqrt(2) - 1, Expr.Pi / 8),
        (Expr.Sqrt(2) + 1, 3 * Expr.Pi / 8),
        (2 - Expr.Sqrt(3), Expr.Pi / 12),
        (2 + Expr.Sqrt(3), 5 * Expr.Pi / 12),
    ];

    private static Expr? InverseTable(Expr x, (Expr Arg, Expr Value)[] table)
    {
        foreach (var (arg, value) in table)
        {
            if (x == arg) return value;
            if (x == -arg) return -value;
        }
        return null;
    }
}
