using System.Numerics;

namespace Equator.Core.Numerics;

/// <summary>Arbitrary-precision complex number.</summary>
public readonly record struct BigComplex(BigFloat Re, BigFloat Im)
{
    public bool IsReal => Im.IsZero;
    public int Precision => Math.Min(Re.Precision, Im.Precision);

    public static BigComplex Real(BigFloat r) => new(r, BigFloat.Zero(r.Precision));

    public static BigComplex operator +(BigComplex a, BigComplex b) => new(a.Re + b.Re, a.Im + b.Im);
    public static BigComplex operator -(BigComplex a, BigComplex b) => new(a.Re - b.Re, a.Im - b.Im);
    public static BigComplex operator -(BigComplex a) => new(-a.Re, -a.Im);

    public static BigComplex operator *(BigComplex a, BigComplex b)
    {
        if (a.IsReal && b.IsReal) return Real(a.Re * b.Re);
        return new(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);
    }

    public static BigComplex operator /(BigComplex a, BigComplex b)
    {
        if (b.IsReal)
        {
            if (b.Re.IsZero) throw new DivideByZeroException();
            return new(a.Re / b.Re, a.Im.IsZero ? a.Im : a.Im / b.Re);
        }
        var den = b.Re * b.Re + b.Im * b.Im;
        return new((a.Re * b.Re + a.Im * b.Im) / den, (a.Im * b.Re - a.Re * b.Im) / den);
    }

    public BigFloat Abs(int precision) => IsReal ? Re.Abs() : BigFloat.Sqrt(Re * Re + Im * Im, precision);
}

/// <summary>
/// Evaluates constant expressions to arbitrary precision (complex-valued, principal branches).
/// Precision is in bits; results carry a few guard bits that the caller rounds away.
/// </summary>
public static class BigEval
{
    public static BigComplex Eval(Expr e, int precision, IReadOnlyDictionary<string, BigComplex>? vars = null)
    {
        Engine.Interrupt.Check();
        int p = precision;
        switch (e)
        {
            case Num n: return BigComplex.Real(BigFloat.FromRational(n.Value, p));
            case Real r: return BigComplex.Real(r.Big is { } big ? big.WithPrecision(Math.Min(p, big.Precision)) : BigFloat.FromDouble(r.Value, p));
            case Sym s:
                switch (s.Name)
                {
                    case "Pi": return BigComplex.Real(BigMath.Pi(p));
                    case "E": return BigComplex.Real(BigMath.E(p));
                    case "I": return new BigComplex(BigFloat.Zero(p), BigFloat.FromInteger(1, p));
                }
                if (vars is not null && vars.TryGetValue(s.Name, out var v)) return v;
                throw new MathException($"Cannot evaluate '{s.Name}' numerically: it has no value.");
            case AddExpr a:
                {
                    var sum = BigComplex.Real(BigFloat.Zero(p));
                    foreach (var t in a.Terms) sum += Eval(t, p, vars);
                    return sum;
                }
            case MulExpr m:
                {
                    var prod = BigComplex.Real(BigFloat.FromInteger(1, p));
                    foreach (var f in m.Factors) prod *= Eval(f, p, vars);
                    return prod;
                }
            case PowExpr pw:
                {
                    var b = Eval(pw.Base, p, vars);
                    if (pw.Exponent is Num { Value.IsInteger: true } ni && BigInteger.Abs(ni.Value.Num) < 1_000_000)
                        return IntPow(b, (int)ni.Value.Num, p);
                    if (pw.Base == Expr.E) return Exp(Eval(pw.Exponent, p, vars), p);
                    return Pow(b, Eval(pw.Exponent, p, vars), p);
                }
            case FnExpr f:
                return Function(f.Name, f.Args.Select(a => Eval(a, p, vars)).ToArray(), p);
        }
        throw new MathException($"Cannot evaluate {e} numerically.");
    }

    public static BigComplex IntPow(BigComplex b, int n, int p)
    {
        if (b.IsReal) return BigComplex.Real(BigMath.PowInt(b.Re, n, p));
        if (n < 0) return One(p) / IntPow(b, -n, p + 16);
        var result = One(p);
        while (n > 0)
        {
            if ((n & 1) == 1) result *= b;
            n >>= 1;
            if (n > 0) b *= b;
        }
        return result;
    }

    private static BigComplex One(int p) => BigComplex.Real(BigFloat.FromInteger(1, p));
    private static BigComplex I(int p) => new(BigFloat.Zero(p), BigFloat.FromInteger(1, p));
    private static BigComplex MulI(BigComplex z) => new(-z.Im, z.Re);

    public static BigComplex Exp(BigComplex z, int p)
    {
        var ea = BigMath.Exp(z.Re, p);
        if (z.IsReal) return BigComplex.Real(ea);
        var (s, c) = BigMath.SinCos(z.Im, p);
        return new(ea * c, ea * s);
    }

    /// <summary>Principal logarithm.</summary>
    public static BigComplex Log(BigComplex z, int p)
    {
        if (z.IsReal && z.Re.Sign > 0) return BigComplex.Real(BigMath.Log(z.Re, p));
        if (z.Re.IsZero && z.Im.IsZero) throw new MathException("Logarithm of zero.");
        var modulus = z.Abs(p + 16);
        return new(BigMath.Log(modulus, p), BigMath.Atan2(z.Im, z.Re, p));
    }

    /// <summary>Principal square root.</summary>
    public static BigComplex Sqrt(BigComplex z, int p)
    {
        if (z.IsReal)
        {
            if (z.Re.Sign >= 0) return BigComplex.Real(BigFloat.Sqrt(z.Re, p));
            return new(BigFloat.Zero(p), BigFloat.Sqrt(-z.Re, p));
        }
        // sqrt(z) = sqrt((|z| + a)/2) + i sign(b) sqrt((|z| − a)/2)
        int wp = p + 16;
        var r = z.Abs(wp);
        var re = BigFloat.Sqrt((r + z.Re.WithPrecision(wp)).Scale(-1), p);
        var im = BigFloat.Sqrt((r - z.Re.WithPrecision(wp)).Scale(-1), p);
        return new(re, z.Im.Sign < 0 ? -im : im);
    }

    public static BigComplex Pow(BigComplex b, BigComplex e, int p)
    {
        if (b.Re.IsZero && b.Im.IsZero)
        {
            if (e.IsReal && e.Re.Sign > 0) return BigComplex.Real(BigFloat.Zero(p));
            throw new MathException("0 raised to a non-positive power.");
        }
        if (e.IsReal && b.IsReal && b.Re.Sign > 0) return BigComplex.Real(BigMath.Pow(b.Re, e.Re, p));
        if (e.IsReal && e.Re.CompareTo(BigFloat.FromRational(BigRational.Half, p)) == 0) return Sqrt(b, p);
        int wp = p + 32;
        return Exp(Log(b, wp) * e, p);
    }

    public static BigComplex Function(string name, BigComplex[] a, int p)
    {
        int wp = p + 32;
        var x = a.Length > 0 ? a[0] : default;
        bool real = a.Length > 0 && x.IsReal;
        BigFloat one = BigFloat.FromInteger(1, wp);
        BigComplex One1 = One(wp);
        switch (name)
        {
            case "exp": return Exp(x, p);
            case "log": return Log(x, p);
            case "sqrt": return Sqrt(x, p);
            case "sin":
                {
                    if (real) return BigComplex.Real(BigMath.SinCos(x.Re, p).Sin);
                    var (s, c) = BigMath.SinCos(x.Re, wp);
                    var ch = Cosh(x.Im, wp); var sh = Sinh(x.Im, wp);
                    return new(s * ch, c * sh);
                }
            case "cos":
                {
                    if (real) return BigComplex.Real(BigMath.SinCos(x.Re, p).Cos);
                    var (s, c) = BigMath.SinCos(x.Re, wp);
                    var ch = Cosh(x.Im, wp); var sh = Sinh(x.Im, wp);
                    return new(c * ch, -(s * sh));
                }
            case "tan": return Function("sin", a, wp) / Function("cos", a, wp);
            case "cot": return Function("cos", a, wp) / Function("sin", a, wp);
            case "sec": return One1 / Function("cos", a, wp);
            case "csc": return One1 / Function("sin", a, wp);
            case "atan":
                if (real) return BigComplex.Real(BigMath.Atan(x.Re, p));
                // atan z = (i/2) (log(1 − i z) − log(1 + i z))
                return MulI(Log(One1 - MulI(x), wp) - Log(One1 + MulI(x), wp)) * Half(wp);
            case "acot": return Function("atan", [One1 / x], p);
            case "asin":
                if (real && x.Re.Abs().CompareTo(one) <= 0)
                {
                    // asin x = atan(x / sqrt(1 − x²)), with ±π/2 at the ends.
                    var d = one - x.Re * x.Re;
                    if (d.IsZero) return BigComplex.Real(x.Re.Sign > 0 ? BigMath.Pi(p).Scale(-1) : -BigMath.Pi(p).Scale(-1));
                    return BigComplex.Real(BigMath.Atan(x.Re / BigFloat.Sqrt(d, wp), p));
                }
                // asin z = −i log(i z + sqrt(1 − z²))
                return MulI(Log(MulI(x) + Sqrt(One1 - x * x, wp), wp)) * BigComplex.Real(BigFloat.FromInteger(-1, wp));
            case "acos":
                {
                    var asin = Function("asin", a, wp);
                    return BigComplex.Real(BigMath.Pi(wp).Scale(-1)) - asin;
                }
            case "asec": return Function("acos", [One1 / x], p);
            case "acsc": return Function("asin", [One1 / x], p);
            case "sinh":
                if (real) return BigComplex.Real(Sinh(x.Re, p));
                return (Exp(x, wp) - Exp(-x, wp)) * Half(wp);
            case "cosh":
                if (real) return BigComplex.Real(Cosh(x.Re, p));
                return (Exp(x, wp) + Exp(-x, wp)) * Half(wp);
            case "tanh": return Function("sinh", a, wp) / Function("cosh", a, wp);
            case "coth": return Function("cosh", a, wp) / Function("sinh", a, wp);
            case "sech": return One1 / Function("cosh", a, wp);
            case "csch": return One1 / Function("sinh", a, wp);
            case "asinh": return Log(x + Sqrt(x * x + One1, wp), p);
            case "acosh": return Log(x + Sqrt(x + One1, wp) * Sqrt(x - One1, wp), p);
            case "atanh": return (Log(One1 + x, wp) - Log(One1 - x, wp)) * Half(wp);
            case "abs": return BigComplex.Real(x.Abs(p));
            case "arg": return BigComplex.Real(BigMath.Atan2(x.Im, x.Re, p));
            case "re": return BigComplex.Real(x.Re);
            case "im": return BigComplex.Real(x.Im);
            case "conj": return new(x.Re, -x.Im);
            case "sign":
                if (real) return BigComplex.Real(BigFloat.FromInteger(x.Re.Sign, p));
                return x / BigComplex.Real(x.Abs(wp));
            case "floor": if (real) return BigComplex.Real(BigFloat.FromInteger(x.Re.Floor(), p)); break;
            case "ceil": if (real) return BigComplex.Real(BigFloat.FromInteger(-(-x.Re).Floor(), p)); break;
            case "round": if (real) return BigComplex.Real(BigFloat.FromInteger(x.Re.Round(), p)); break;
            case "gamma": if (real) return BigComplex.Real(BigMath.Gamma(x.Re, p)); break;
            case "factorial": if (real) return BigComplex.Real(BigMath.Gamma(x.Re + BigFloat.FromInteger(1, wp), p)); break;
            case "erf": if (real) return BigComplex.Real(BigMath.Erf(x.Re, p)); break;
            case "zeta": if (real) return BigComplex.Real(ZetaFunction.Zeta(x.Re, p)); break;
            case "atan2":
                if (a.Length == 2 && a[0].IsReal && a[1].IsReal) return BigComplex.Real(BigMath.Atan2(a[0].Re, a[1].Re, p));
                break;
            case "binomial":
                if (a.Length == 2 && a[0].IsReal && a[1].IsReal)
                {
                    var n = a[0].Re; var k = a[1].Re;
                    var g = BigMath.Gamma(n + one, wp) / (BigMath.Gamma(k + one, wp) * BigMath.Gamma(n - k + one, wp));
                    return BigComplex.Real(g.WithPrecision(p));
                }
                break;
        }
        throw new MathException($"Cannot evaluate {name} to arbitrary precision for this argument.");
    }

    private static BigComplex Half(int p) => BigComplex.Real(BigFloat.FromRational(BigRational.Half, p));

    private static BigFloat Sinh(BigFloat x, int p)
    {
        int wp = p + 32 + (int)Math.Max(0, -x.Log2Magnitude); // cancellation near 0
        var e = BigMath.Exp(x.WithPrecision(wp), wp);
        return ((e - BigFloat.Divide(BigFloat.FromInteger(1, wp), e, wp)).Scale(-1)).WithPrecision(p);
    }

    private static BigFloat Cosh(BigFloat x, int p)
    {
        int wp = p + 32;
        var e = BigMath.Exp(x.WithPrecision(wp), wp);
        return ((e + BigFloat.Divide(BigFloat.FromInteger(1, wp), e, wp)).Scale(-1)).WithPrecision(p);
    }

    // ---- conversion to expressions -------------------------------------------------------------------

    /// <summary>A Real (or Real + Real·I) expression carrying the given number of decimal digits.</summary>
    public static Expr ToExpr(BigComplex z, int digits)
    {
        int bits = BigFloat.DigitsToBits(digits);
        var re = z.Re.WithPrecision(bits);
        var im = z.Im.WithPrecision(bits);
        // Drop components that are pure rounding noise relative to the other one.
        if (!im.IsZero && !re.IsZero)
        {
            if (im.Log2Magnitude < re.Log2Magnitude - bits) im = BigFloat.Zero(bits);
            else if (re.Log2Magnitude < im.Log2Magnitude - bits) re = BigFloat.Zero(bits);
        }
        var r = new Real(re, digits);
        if (im.IsZero) return r;
        var i = Expr.Times(new Real(im, digits), Expr.I);
        return re.IsZero ? i : Expr.Plus(r, i);
    }

    /// <summary>The working precision (digits) of the arbitrary-precision reals in e; null when there are none or machine reals too.</summary>
    public static int? PrecisionOf(Expr e)
    {
        int? digits = null;
        bool machine = false;
        foreach (var s in e.Subexpressions())
        {
            if (s is not Real r) continue;
            if (r.Big is null) { machine = true; break; }
            digits = digits is null ? r.Digits : Math.Min(digits.Value, r.Digits);
        }
        return machine ? null : digits;
    }
}
