using System.Numerics;

namespace Equator.Core.Numerics;

/// <summary>
/// Riemann zeta function for real arguments: Borwein's accelerated alternating series for the Dirichlet eta
/// function, ζ(s) = η(s) / (1 − 2^(1−s)), and the functional equation for s &lt; 1/2.
/// </summary>
public static class ZetaFunction
{
    /// <summary>Borwein coefficients d_k = n Σ_{i≤k} (n+i−1)! 4^i / ((n−i)! (2i)!) as exact integers.</summary>
    private static BigInteger[] Coefficients(int n)
    {
        var d = new BigInteger[n + 1];
        BigInteger sum = 0;
        for (int i = 0; i <= n; i++)
        {
            var term = IntMath.Factorial(n + i - 1 < 0 ? 0 : n + i - 1) * BigInteger.Pow(4, i) / (IntMath.Factorial(n - i) * IntMath.Factorial(2 * i));
            sum += term;
            d[i] = n * sum;
        }
        return d;
    }

    public static double Zeta(double s)
    {
        if (s == 1) return double.PositiveInfinity;
        if (s < 0.5)
        {
            // ζ(s) = 2^s π^(s−1) sin(πs/2) Γ(1−s) ζ(1−s)
            return Math.Pow(2, s) * Math.Pow(Math.PI, s - 1) * Math.Sin(Math.PI * s / 2) * NumericEval.Gamma(1 - s) * Zeta(1 - s);
        }
        const int n = 40;
        var d = Coefficients(n);
        double dn = (double)d[n], sum = 0;
        for (int k = 0; k < n; k++)
        {
            double term = ((double)(d[k] - d[n])) / Math.Pow(k + 1, s);
            sum += (k & 1) == 0 ? term : -term;
        }
        double eta = -sum / dn;
        return eta / (1 - Math.Pow(2, 1 - s));
    }

    public static BigFloat Zeta(BigFloat s, int precision)
    {
        int wp = precision + 48;
        var one = BigFloat.FromInteger(1, wp);
        var sr = s.WithPrecision(wp);
        if (sr.CompareTo(one) == 0) throw new MathException("zeta has a pole at 1.");
        var half = BigFloat.FromRational(BigRational.Half, wp);
        if (sr.CompareTo(half) < 0)
        {
            var pi = BigMath.Pi(wp);
            var two = BigFloat.FromInteger(2, wp);
            var (sin, _) = BigMath.SinCos(pi * sr * half, wp);
            var r = BigMath.Pow(two, sr, wp) * BigMath.Pow(pi, sr - one, wp) * sin * BigMath.Gamma(one - sr, wp) * Zeta(one - sr, wp);
            return r.WithPrecision(precision);
        }
        // Error ≤ 3 / (3 + √8)^n: each term gives about 0.77 decimal digits.
        int n = (int)(BigFloat.BitsToDigits(wp) * 1.31) + 4;
        var d = Coefficients(n);
        var sum = BigFloat.Zero(wp);
        for (int k = 0; k < n; k++)
        {
            Engine.Interrupt.Check();
            var term = BigFloat.FromInteger(d[k] - d[n], wp) / BigMath.Pow(BigFloat.FromInteger(k + 1, wp), sr, wp);
            sum = (k & 1) == 0 ? sum + term : sum - term;
        }
        var eta = -(sum / BigFloat.FromInteger(d[n], wp));
        var denom = one - BigMath.Pow(BigFloat.FromInteger(2, wp), one - sr, wp);
        return (eta / denom).WithPrecision(precision);
    }
}
