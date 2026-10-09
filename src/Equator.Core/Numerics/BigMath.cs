using System.Numerics;

namespace Equator.Core.Numerics;

/// <summary>
/// Elementary and special functions on <see cref="BigFloat"/> to a requested precision (bits). Each function
/// works with guard bits and rounds the result to the requested precision.
/// </summary>
public static class BigMath
{
    private const int Guard = 32;

    // ---- constants (cached at the highest precision computed so far) -------------------------------

    private static BigFloat _pi, _ln2;
    private static readonly object Lock = new();

    public static BigFloat Pi(int precision)
    {
        lock (Lock)
        {
            if (_pi.Precision >= precision + Guard && !_pi.IsZero) return _pi.WithPrecision(precision);
        }
        int wp = precision + Guard + 16;
        // Machin: π = 16 atan(1/5) − 4 atan(1/239)
        var pi = AtanInverse(5, wp) * 16 - AtanInverse(239, wp) * 4;
        lock (Lock) _pi = pi;
        return pi.WithPrecision(precision);
    }

    public static BigFloat Ln2(int precision)
    {
        lock (Lock)
        {
            if (_ln2.Precision >= precision + Guard && !_ln2.IsZero) return _ln2.WithPrecision(precision);
        }
        int wp = precision + Guard + 16;
        // ln 2 = 2 atanh(1/3) = 2 Σ 1/((2k+1) 3^(2k+1))
        var ln2 = AtanhInverse(3, wp) * 2;
        lock (Lock) _ln2 = ln2;
        return ln2.WithPrecision(precision);
    }

    public static BigFloat E(int precision) => Exp(BigFloat.FromInteger(1, precision + Guard), precision);

    /// <summary>atan(1/n) = Σ (−1)^k / ((2k+1) n^(2k+1)) for an integer n ≥ 2.</summary>
    private static BigFloat AtanInverse(long n, int wp)
    {
        var one = BigFloat.FromInteger(1, wp);
        var power = BigFloat.Divide(one, BigFloat.FromInteger(n, wp), wp); // 1/n^(2k+1)
        var n2 = BigFloat.FromInteger(n * n, wp);
        var sum = power;
        for (long k = 1; ; k++)
        {
            power = BigFloat.Divide(power, n2, wp);
            var term = BigFloat.Divide(power, BigFloat.FromInteger(2 * k + 1, wp), wp);
            if (term.IsZero || term.Log2Magnitude < sum.Log2Magnitude - wp - 2) break;
            sum = (k & 1) == 1 ? sum - term : sum + term;
        }
        return sum;
    }

    /// <summary>atanh(1/n) = Σ 1/((2k+1) n^(2k+1)).</summary>
    private static BigFloat AtanhInverse(long n, int wp)
    {
        var one = BigFloat.FromInteger(1, wp);
        var power = BigFloat.Divide(one, BigFloat.FromInteger(n, wp), wp);
        var n2 = BigFloat.FromInteger(n * n, wp);
        var sum = power;
        for (long k = 1; ; k++)
        {
            power = BigFloat.Divide(power, n2, wp);
            var term = BigFloat.Divide(power, BigFloat.FromInteger(2 * k + 1, wp), wp);
            if (term.IsZero || term.Log2Magnitude < sum.Log2Magnitude - wp - 2) break;
            sum += term;
        }
        return sum;
    }

    // ---- exp and log --------------------------------------------------------------------------------

    public static BigFloat Exp(BigFloat x, int precision)
    {
        if (x.IsZero) return BigFloat.FromInteger(1, precision);
        if (x.Log2Magnitude > 40) throw new MathException(Messages.T("Number too large for exp."));
        // x = k ln 2 + r, |r| ≤ ln2/2; then exp(r) = exp(r / 2^s)^(2^s).
        long magnitude = Math.Max(0, x.Log2Magnitude + 2);
        int wp = precision + Guard + (int)magnitude;
        var ln2 = Ln2(wp);
        var k = BigFloat.Divide(x.WithPrecision(wp), ln2, wp).Round();
        var r = x.WithPrecision(wp) - BigFloat.FromInteger(k, wp) * ln2;
        int s = Math.Max(0, (int)Math.Sqrt(wp) / 2);
        wp += s;
        r = r.WithPrecision(wp).Scale(-s);
        // Taylor series.
        var one = BigFloat.FromInteger(1, wp);
        var sum = one;
        var term = one;
        for (int n = 1; n < 100000; n++)
        {
            term = BigFloat.Divide(term * r, BigFloat.FromInteger(n, wp), wp);
            if (term.IsZero || term.Log2Magnitude < -wp - 4) break;
            sum += term;
        }
        for (int i = 0; i < s; i++) sum *= sum;
        return sum.Scale((long)k).WithPrecision(precision);
    }

    public static BigFloat Log(BigFloat x, int precision)
    {
        if (x.Sign <= 0) throw new MathException(Messages.T("Logarithm of a non-positive number."));
        int wp = precision + Guard;
        // x = m · 2^e with m in [1/sqrt2, sqrt2): log x = e ln2 + 2 atanh((m − 1)/(m + 1)).
        long e = x.Log2Magnitude;
        var m = x.WithPrecision(wp).Scale(-e); // in [1, 2)
        if (m.ToDouble() > 1.4142135623730951) { m = m.Scale(-1); e++; }
        var one = BigFloat.FromInteger(1, wp);
        var z = BigFloat.Divide(m - one, m + one, wp);
        var z2 = z * z;
        var sum = z;
        var power = z;
        for (long k = 1; k < 1000000; k++)
        {
            power *= z2;
            var term = BigFloat.Divide(power, BigFloat.FromInteger(2 * k + 1, wp), wp);
            if (term.IsZero || term.Log2Magnitude < sum.Log2Magnitude - wp - 4) break;
            sum += term;
        }
        var result = sum * 2 + Ln2(wp + 64) * e;
        return result.WithPrecision(precision);
    }

    public static BigFloat Sqrt(BigFloat x, int precision) => BigFloat.Sqrt(x, precision);

    /// <summary>x^y for x &gt; 0 (or integer y).</summary>
    public static BigFloat Pow(BigFloat x, BigFloat y, int precision)
    {
        if (y.Exponent >= 0 && BigInteger.Abs(y.Mantissa).GetBitLength() + y.Exponent < 31)
            return PowInt(x, (int)y.Floor(), precision);
        int wp = precision + Guard + (int)Math.Max(0, y.Log2Magnitude + 4);
        return Exp(Log(x, wp) * y.WithPrecision(wp), precision);
    }

    public static BigFloat PowInt(BigFloat x, int n, int precision)
    {
        int wp = precision + Guard + 2 * (int)Math.Log2(Math.Abs((long)n) + 1);
        if (n < 0) return BigFloat.Divide(BigFloat.FromInteger(1, wp), PowInt(x, -n, wp), precision);
        var result = BigFloat.FromInteger(1, wp);
        var b = x.WithPrecision(wp);
        while (n > 0)
        {
            if ((n & 1) == 1) result *= b;
            n >>= 1;
            if (n > 0) b *= b;
        }
        return result.WithPrecision(precision);
    }

    // ---- trigonometric ------------------------------------------------------------------------------

    /// <summary>(sin x, cos x).</summary>
    public static (BigFloat Sin, BigFloat Cos) SinCos(BigFloat x, int precision)
    {
        int wp = precision + Guard + (int)Math.Max(0, x.Log2Magnitude + 2);
        if (x.IsZero) return (BigFloat.Zero(precision), BigFloat.FromInteger(1, precision));
        // Reduce modulo π/2: x = q π/2 + r, |r| ≤ π/4.
        var halfPi = Pi(wp).Scale(-1);
        var q = BigFloat.Divide(x.WithPrecision(wp), halfPi, wp).Round();
        var r = x.WithPrecision(wp) - BigFloat.FromInteger(q, wp) * halfPi;
        // Halve r a few times for faster convergence, then use double-angle formulas.
        int s = Math.Max(0, (int)Math.Sqrt(wp) / 3);
        wp += 2 * s;
        r = r.WithPrecision(wp).Scale(-s);
        var r2 = r * r;
        // sin series
        var sin = r; var term = r;
        for (int n = 1; n < 100000; n++)
        {
            term = BigFloat.Divide(term * r2, BigFloat.FromInteger((2L * n) * (2L * n + 1), wp), wp);
            if (term.IsZero || term.Log2Magnitude < -wp - 4) break;
            sin = (n & 1) == 1 ? sin - term : sin + term;
        }
        var one = BigFloat.FromInteger(1, wp);
        var cos = BigFloat.Sqrt(one - sin * sin, wp);
        for (int i = 0; i < s; i++)
        {
            var newSin = (sin * cos).Scale(1);
            var newCos = one - (sin * sin).Scale(1);
            sin = newSin; cos = newCos;
        }
        int quadrant = (int)(((q % 4) + 4) % 4);
        (BigFloat S, BigFloat C) res = quadrant switch
        {
            0 => (sin, cos),
            1 => (cos, -sin),
            2 => (-sin, -cos),
            _ => (-cos, sin),
        };
        return (res.S.WithPrecision(precision), res.C.WithPrecision(precision));
    }

    public static BigFloat Atan(BigFloat x, int precision)
    {
        int wp = precision + Guard;
        if (x.IsZero) return BigFloat.Zero(precision);
        var one = BigFloat.FromInteger(1, wp);
        if (x.Abs().CompareTo(one) > 0)
        {
            // atan x = ±π/2 − atan(1/x)
            var inner = Atan(BigFloat.Divide(one, x.WithPrecision(wp), wp), wp);
            var halfPi = Pi(wp).Scale(-1);
            return (x.Sign > 0 ? halfPi - inner : -halfPi - inner).WithPrecision(precision);
        }
        // Argument halving: atan x = 2 atan(x / (1 + sqrt(1 + x²))).
        var y = x.WithPrecision(wp);
        int doublings = 0;
        while (y.Log2Magnitude > -12 && doublings < 64)
        {
            y = BigFloat.Divide(y, one + BigFloat.Sqrt(one + y * y, wp), wp);
            doublings++;
        }
        var y2 = y * y;
        var sum = y; var power = y;
        for (long k = 1; k < 1000000; k++)
        {
            power *= y2;
            var term = BigFloat.Divide(power, BigFloat.FromInteger(2 * k + 1, wp), wp);
            if (term.IsZero || term.Log2Magnitude < sum.Log2Magnitude - wp - 4) break;
            sum = (k & 1) == 1 ? sum - term : sum + term;
        }
        return sum.Scale(doublings).WithPrecision(precision);
    }

    /// <summary>atan2(y, x) in (−π, π].</summary>
    public static BigFloat Atan2(BigFloat y, BigFloat x, int precision)
    {
        int wp = precision + Guard;
        if (x.IsZero)
        {
            if (y.IsZero) return BigFloat.Zero(precision);
            var hp = Pi(precision).Scale(-1);
            return y.Sign > 0 ? hp : -hp;
        }
        var a = Atan(BigFloat.Divide(y.WithPrecision(wp), x.WithPrecision(wp), wp), wp);
        if (x.Sign > 0) return a.WithPrecision(precision);
        var pi = Pi(wp);
        return (y.Sign >= 0 ? a + pi : a - pi).WithPrecision(precision);
    }

    // ---- special functions --------------------------------------------------------------------------

    /// <summary>Γ(x) for real x (reflection for x &lt; 1/2, Stirling series after shifting the argument up).</summary>
    public static BigFloat Gamma(BigFloat x, int precision)
    {
        int wp = precision + Guard + 16;
        var one = BigFloat.FromInteger(1, wp);
        var xr = x.WithPrecision(wp);
        if (xr.CompareTo(BigFloat.FromRational(BigRational.Half, wp)) < 0)
        {
            // Γ(x) = π / (sin(πx) Γ(1 − x))
            var (s, _) = SinCos(Pi(wp) * xr, wp);
            if (s.IsZero || s.Log2Magnitude < -wp + 8) throw new MathException(Messages.T("Gamma has a pole here."));
            return BigFloat.Divide(Pi(wp), s * Gamma(one - xr, wp), precision);
        }
        // Shift: Γ(x) = Γ(x + N) / (x (x+1) ... (x+N−1)), with x + N large enough for the Stirling series.
        int digits = BigFloat.BitsToDigits(wp);
        int N = Math.Max(0, (int)Math.Ceiling(digits * 1.1 - x.ToDouble()));
        var product = one;
        var z = xr;
        for (int i = 0; i < N; i++) { product *= z; z += one; }
        // ln Γ(z) = (z − 1/2) ln z − z + ln(2π)/2 + Σ B_2k / (2k (2k − 1) z^(2k−1))
        var lnz = Log(z, wp);
        var half = BigFloat.FromRational(BigRational.Half, wp);
        var lg = (z - half) * lnz - z + Log(Pi(wp).Scale(1), wp) * half;
        var zinv = BigFloat.Divide(one, z, wp);
        var zinv2 = zinv * zinv;
        var zpow = zinv;
        for (int k = 1; k < 2000; k++)
        {
            var b = Calculus.Sums.Bernoulli(2 * k);
            var coef = BigFloat.FromRational(b / (2 * k * (2 * k - 1)), wp);
            var term = coef * zpow;
            if (term.IsZero || term.Log2Magnitude < lg.Log2Magnitude - wp - 4) break;
            lg += term;
            zpow *= zinv2;
        }
        return BigFloat.Divide(Exp(lg, wp), product, precision);
    }

    /// <summary>erf(x) by its Taylor series, with extra working precision for the cancellation at large |x|.</summary>
    public static BigFloat Erf(BigFloat x, int precision)
    {
        if (x.IsZero) return BigFloat.Zero(precision);
        double xd = x.ToDouble();
        if (xd * xd > (precision + 8) * 0.6931471805599453 + 10)
            return BigFloat.FromInteger(Math.Sign(xd), precision); // erfc(x) < 2^-precision
        int wp = precision + Guard + (int)(xd * xd * 1.4427 + 8);
        var xr = x.WithPrecision(wp);
        var x2 = xr * xr;
        var sum = xr; var term = xr;
        for (long n = 1; n < 1000000; n++)
        {
            term = BigFloat.Divide(term * x2, BigFloat.FromInteger(n, wp), wp);
            var add = BigFloat.Divide(term, BigFloat.FromInteger(2 * n + 1, wp), wp);
            if (add.IsZero || add.Log2Magnitude < sum.Log2Magnitude - wp - 4) break;
            sum = (n & 1) == 1 ? sum - add : sum + add;
        }
        var factor = BigFloat.Divide(BigFloat.FromInteger(2, wp), BigFloat.Sqrt(Pi(wp), wp), wp);
        return (sum * factor).WithPrecision(precision);
    }
}
