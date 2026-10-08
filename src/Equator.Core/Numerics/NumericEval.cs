using System.Numerics;

namespace Equator.Core.Numerics;

/// <summary>Floating-point evaluation of expressions (complex-valued), plus compilation to fast real delegates for plotting.</summary>
public static class NumericEval
{
    public static Complex Eval(Expr e, IReadOnlyDictionary<string, Complex>? vars = null)
    {
        switch (e)
        {
            case Num n: return n.Value.ToDouble();
            case Real r: return r.Value;
            case Sym s:
                switch (s.Name)
                {
                    case "Pi": return Math.PI;
                    case "E": return Math.E;
                    case "I": return Complex.ImaginaryOne;
                    case "Infinity": return double.PositiveInfinity;
                    case "ComplexInfinity": return double.PositiveInfinity;
                    case "Undefined": return double.NaN;
                }
                if (vars is not null && vars.TryGetValue(s.Name, out var v)) return v;
                throw new MathException($"Cannot evaluate '{s.Name}' numerically: it has no value.");
            case AddExpr a:
                {
                    Complex sum = 0;
                    foreach (var t in a.Terms) sum += Eval(t, vars);
                    return sum;
                }
            case MulExpr m:
                {
                    Complex prod = 1;
                    foreach (var f in m.Factors) prod *= Eval(f, vars);
                    return prod;
                }
            case PowExpr p:
                {
                    var b = Eval(p.Base, vars);
                    if (p.Exponent is Num { Value.IsInteger: true } ni && BigInteger.Abs(ni.Value.Num) < 1000)
                        return IntPow(b, (int)ni.Value.Num);
                    var x = Eval(p.Exponent, vars);
                    if (b.Imaginary == 0 && x.Imaginary == 0 && b.Real >= 0) return Math.Pow(b.Real, x.Real);
                    return Complex.Pow(b, x);
                }
            case FnExpr f:
                return EvalFunction(f.Name, f.Args.Select(a => Eval(a, vars)).ToArray());
        }
        throw new MathException($"Cannot evaluate {e} numerically.");
    }

    public static double EvalReal(Expr e)
    {
        var c = Eval(e);
        return Math.Abs(c.Imaginary) <= 1e-12 * Math.Max(1, Math.Abs(c.Real)) ? c.Real : double.NaN;
    }

    private static Complex IntPow(Complex b, int n)
    {
        if (n < 0) return 1 / IntPow(b, -n);
        if (b.Imaginary == 0) return Math.Pow(b.Real, n);
        Complex r = 1;
        while (n > 0)
        {
            if ((n & 1) == 1) r *= b;
            b *= b;
            n >>= 1;
        }
        return r;
    }

    /// <summary>Converts a complex value into a Real or Real + Real*I expression.</summary>
    public static Expr FromComplex(Complex c)
    {
        if (double.IsNaN(c.Real) || double.IsNaN(c.Imaginary)) return Expr.Undefined;
        if (double.IsInfinity(c.Real) && c.Imaginary == 0) return c.Real > 0 ? Expr.Infinity : -Expr.Infinity;
        if (double.IsInfinity(c.Real) || double.IsInfinity(c.Imaginary)) return Expr.ComplexInfinity;
        double scale = Math.Max(Math.Abs(c.Real), Math.Abs(c.Imaginary));
        double re = Math.Abs(c.Real) < 1e-15 * scale ? 0 : c.Real;
        double im = Math.Abs(c.Imaginary) < 1e-15 * scale ? 0 : c.Imaginary;
        if (im == 0) return new Real(re);
        if (re == 0) return Expr.Times(new Real(im), Expr.I);
        return Expr.Plus(new Real(re), Expr.Times(new Real(im), Expr.I));
    }

    public static Complex EvalFunction(string name, Complex[] a)
    {
        Complex x = a.Length > 0 ? a[0] : 0;
        bool real = x.Imaginary == 0;
        double r = x.Real;
        switch (name)
        {
            case "sin": return real ? Math.Sin(r) : Complex.Sin(x);
            case "cos": return real ? Math.Cos(r) : Complex.Cos(x);
            case "tan": return real ? Math.Tan(r) : Complex.Tan(x);
            case "cot": return real ? 1 / Math.Tan(r) : 1 / Complex.Tan(x);
            case "sec": return real ? 1 / Math.Cos(r) : 1 / Complex.Cos(x);
            case "csc": return real ? 1 / Math.Sin(r) : 1 / Complex.Sin(x);
            case "asin": return real && Math.Abs(r) <= 1 ? Math.Asin(r) : Complex.Asin(x);
            case "acos": return real && Math.Abs(r) <= 1 ? Math.Acos(r) : Complex.Acos(x);
            case "atan": return real ? Math.Atan(r) : Complex.Atan(x);
            case "acot": return real ? Math.Atan(1 / r) : Complex.Atan(1 / x);
            case "asec": return real && Math.Abs(r) >= 1 ? Math.Acos(1 / r) : Complex.Acos(1 / x);
            case "acsc": return real && Math.Abs(r) >= 1 ? Math.Asin(1 / r) : Complex.Asin(1 / x);
            case "sinh": return real ? Math.Sinh(r) : Complex.Sinh(x);
            case "cosh": return real ? Math.Cosh(r) : Complex.Cosh(x);
            case "tanh": return real ? Math.Tanh(r) : Complex.Tanh(x);
            case "coth": return 1 / (real ? Math.Tanh(r) : Complex.Tanh(x));
            case "sech": return 1 / (real ? Math.Cosh(r) : Complex.Cosh(x));
            case "csch": return 1 / (real ? Math.Sinh(r) : Complex.Sinh(x));
            case "asinh": return real ? Math.Asinh(r) : Complex.Log(x + Complex.Sqrt(x * x + 1));
            case "acosh": return real && r >= 1 ? Math.Acosh(r) : Complex.Log(x + Complex.Sqrt(x + 1) * Complex.Sqrt(x - 1));
            case "atanh": return real && Math.Abs(r) < 1 ? Math.Atanh(r) : 0.5 * Complex.Log((1 + x) / (1 - x));
            case "log": return real && r > 0 ? Math.Log(r) : Complex.Log(x);
            case "exp": return real ? Math.Exp(r) : Complex.Exp(x);
            case "sqrt": return real && r >= 0 ? Math.Sqrt(r) : Complex.Sqrt(x);
            case "abs": return Complex.Abs(x);
            case "arg": return x.Phase;
            case "re": return x.Real;
            case "im": return x.Imaginary;
            case "conj": return Complex.Conjugate(x);
            case "sign": return real ? Math.Sign(r) : x / Complex.Abs(x);
            case "floor": return Math.Floor(r);
            case "ceil": return Math.Ceiling(r);
            case "round": return Math.Floor(r + 0.5);
            case "gamma": return Gamma(r);
            case "factorial": return Gamma(r + 1);
            case "erf": return Erf(r);
            case "atan2": return Math.Atan2(a[0].Real, a[1].Real);
            case "max": return a.Max(c => c.Real);
            case "min": return a.Min(c => c.Real);
            case "mod": return a[0].Real - a[1].Real * Math.Floor(a[0].Real / a[1].Real);
            case "binomial": return Gamma(a[0].Real + 1) / (Gamma(a[1].Real + 1) * Gamma(a[0].Real - a[1].Real + 1));
        }
        throw new MathException($"Cannot evaluate function '{name}' numerically.");
    }

    // Lanczos approximation (g = 7, n = 9).
    private static readonly double[] LanczosCoef =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    public static double Gamma(double x)
    {
        if (x == Math.Floor(x) && x <= 0) return double.NaN;
        if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x));
        if (x > 171.7) return double.PositiveInfinity;
        if (x == Math.Floor(x) && x < 171)
        {
            double f = 1;
            for (int i = 2; i < (int)x; i++) f *= i;
            return f;
        }
        x -= 1;
        double acc = LanczosCoef[0];
        double t = x + 7.5;
        for (int i = 1; i < 9; i++) acc += LanczosCoef[i] / (x + i);
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * acc;
    }

    public static double Erf(double x)
    {
        // Series for small |x|, continued fraction (via erfc) for large |x|; ~1e-15 accuracy.
        double ax = Math.Abs(x);
        if (ax < 2.5)
        {
            double sum = x, term = x, x2 = x * x;
            for (int n = 1; n < 200; n++)
            {
                term *= -x2 / n;
                double add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
            }
            return 2 / Math.Sqrt(Math.PI) * sum;
        }
        // Lentz continued fraction for erfc.
        double f = ax, c = ax, d = 0, tiny = 1e-300;
        for (int k = 1; k < 300; k++)
        {
            double an = k / 2.0;
            d = ax + an * d; if (Math.Abs(d) < tiny) d = tiny; d = 1 / d;
            c = ax + an / c; if (Math.Abs(c) < tiny) c = tiny;
            double delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        double erfc = Math.Exp(-ax * ax) / Math.Sqrt(Math.PI) / f;
        return Math.Sign(x) * (1 - erfc);
    }

    // ---- compilation to real-valued delegates (used by plotting and numeric algorithms) --------

    /// <summary>Compiles an expression in the given variables to a real-valued function. Non-real results become NaN.</summary>
    public static Func<double[], double> Compile(Expr e, params string[] vars)
    {
        var index = vars.Select((v, i) => (v, i)).ToDictionary(t => t.v, t => t.i);
        return CompileNode(e, index);
    }

    public static Func<double, double> Compile1(Expr e, string var)
    {
        var f = Compile(e, var);
        return x => f([x]);
    }

    private static Func<double[], double> CompileNode(Expr e, Dictionary<string, int> idx)
    {
        switch (e)
        {
            case Num n: { var v = n.Value.ToDouble(); return _ => v; }
            case Real r: { var v = r.Value; return _ => v; }
            case Sym s:
                {
                    if (idx.TryGetValue(s.Name, out var i)) return x => x[i];
                    double v = s.Name switch
                    {
                        "Pi" => Math.PI,
                        "E" => Math.E,
                        "Infinity" => double.PositiveInfinity,
                        _ => double.NaN,
                    };
                    return _ => v;
                }
            case AddExpr a:
                {
                    var parts = a.Terms.Select(t => CompileNode(t, idx)).ToArray();
                    return x => { double s = 0; foreach (var p in parts) s += p(x); return s; };
                }
            case MulExpr m:
                {
                    // I*f(x) is non-real unless f is zero.
                    if (m.Factors.Contains(Expr.I)) return _ => double.NaN;
                    var parts = m.Factors.Select(t => CompileNode(t, idx)).ToArray();
                    return x => { double s = 1; foreach (var p in parts) s *= p(x); return s; };
                }
            case PowExpr p:
                {
                    var b = CompileNode(p.Base, idx);
                    if (p.Exponent is Num { Value.IsInteger: true } ni && BigInteger.Abs(ni.Value.Num) < 1000)
                    {
                        int k = (int)ni.Value.Num;
                        return k switch
                        {
                            2 => x => { var v = b(x); return v * v; },
                            -1 => x => 1 / b(x),
                            _ => x => Math.Pow(b(x), k),
                        };
                    }
                    if (p.Exponent is Num { Value: var q } && !q.Den.IsEven)
                    {
                        // Odd roots of negative numbers: use the real root, as users plotting x^(1/3) expect.
                        double qe = q.ToDouble();
                        bool oddNum = !q.Num.IsEven;
                        return x =>
                        {
                            var v = b(x);
                            if (v >= 0) return Math.Pow(v, qe);
                            var mag = Math.Pow(-v, qe);
                            return oddNum ? -mag : mag;
                        };
                    }
                    if (p.Base == Expr.E)
                    {
                        var ex = CompileNode(p.Exponent, idx);
                        return x => Math.Exp(ex(x));
                    }
                    var e2 = CompileNode(p.Exponent, idx);
                    return x => Math.Pow(b(x), e2(x));
                }
            case FnExpr f:
                {
                    var args = f.Args.Select(t => CompileNode(t, idx)).ToArray();
                    Func<double, double>? fn = f.Name switch
                    {
                        "sin" => Math.Sin,
                        "cos" => Math.Cos,
                        "tan" => Math.Tan,
                        "cot" => v => 1 / Math.Tan(v),
                        "sec" => v => 1 / Math.Cos(v),
                        "csc" => v => 1 / Math.Sin(v),
                        "asin" => Math.Asin,
                        "acos" => Math.Acos,
                        "atan" => Math.Atan,
                        "acot" => v => Math.Atan(1 / v),
                        "sinh" => Math.Sinh,
                        "cosh" => Math.Cosh,
                        "tanh" => Math.Tanh,
                        "asinh" => Math.Asinh,
                        "acosh" => Math.Acosh,
                        "atanh" => Math.Atanh,
                        "log" => Math.Log,
                        "abs" => Math.Abs,
                        "sign" => v => Math.Sign(v),
                        "floor" => Math.Floor,
                        "ceil" => Math.Ceiling,
                        "round" => v => Math.Floor(v + 0.5),
                        "gamma" => Gamma,
                        "factorial" => v => Gamma(v + 1),
                        "erf" => Erf,
                        _ => null,
                    };
                    if (fn is not null && args.Length == 1)
                    {
                        var a0 = args[0];
                        return x => fn(a0(x));
                    }
                    var name = f.Name;
                    return x =>
                    {
                        try
                        {
                            var c = EvalFunction(name, args.Select(a => (Complex)a(x)).ToArray());
                            return c.Imaginary == 0 ? c.Real : double.NaN;
                        }
                        catch (MathException) { return double.NaN; }
                    };
                }
        }
        return _ => double.NaN;
    }
}
