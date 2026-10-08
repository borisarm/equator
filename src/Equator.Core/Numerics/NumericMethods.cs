using System.Numerics;

namespace Equator.Core.Numerics;

public static class NumericMethods
{
    /// <summary>All complex roots of sum a[i] x^i (Durand-Kerner iteration followed by Newton polishing).</summary>
    public static List<Complex> PolynomialRoots(double[] a)
    {
        int n = a.Length - 1;
        while (n > 0 && a[n] == 0) n--;
        var roots = new List<Complex>();
        if (n < 1) return roots;
        // Factor out zero roots exactly.
        int zeros = 0;
        while (zeros < n && a[zeros] == 0) zeros++;
        for (int i = 0; i < zeros; i++) roots.Add(Complex.Zero);
        var c = a.Skip(zeros).Take(n - zeros + 1).Select(v => v / a[n]).ToArray();
        int m = c.Length - 1;
        if (m < 1) return roots;

        Complex P(Complex x)
        {
            Complex r = 1;
            for (int i = m - 1; i >= 0; i--) r = r * x + c[i];
            return r;
        }
        Complex dP(Complex x)
        {
            Complex r = m;
            for (int i = m - 1; i >= 1; i--) r = r * x + i * c[i];
            return r;
        }

        double radius = 1 + c.Take(m).Max(Math.Abs);
        var z = new Complex[m];
        for (int i = 0; i < m; i++)
            z[i] = Complex.FromPolarCoordinates(Math.Min(radius, 10) * 0.9, 2 * Math.PI * i / m + 0.4);
        for (int iter = 0; iter < 5000; iter++)
        {
            double change = 0;
            for (int i = 0; i < m; i++)
            {
                Complex den = 1;
                for (int j = 0; j < m; j++) if (j != i) den *= z[i] - z[j];
                if (den == Complex.Zero) den = 1e-14;
                var delta = P(z[i]) / den;
                z[i] -= delta;
                change = Math.Max(change, delta.Magnitude / Math.Max(1, z[i].Magnitude));
            }
            if (change < 1e-16) break;
        }
        // Newton polish.
        for (int i = 0; i < m; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                var d = dP(z[i]);
                if (d == Complex.Zero) break;
                z[i] -= P(z[i]) / d;
            }
            double re = z[i].Real, im = z[i].Imaginary;
            if (Math.Abs(im) < 1e-9 * Math.Max(1, Math.Abs(re))) im = 0;
            if (Math.Abs(re) < 1e-13 * Math.Max(1, Math.Abs(im))) re = 0;
            roots.Add(new Complex(re, im));
        }
        return roots.OrderBy(r => r.Imaginary != 0).ThenBy(r => r.Real).ThenBy(r => r.Imaginary).ToList();
    }

    /// <summary>Finds real roots of f in [a, b] by sampling for sign changes and refining with Brent's method.</summary>
    public static List<double> RealRoots(Func<double, double> f, double a, double b, int samples = 4000)
    {
        var roots = new List<double>();
        double h = (b - a) / samples;
        double x0 = a, f0 = f(x0);
        double prevAbs = Math.Abs(f0);
        for (int i = 1; i <= samples; i++)
        {
            double x1 = a + i * h, f1 = f(x1);
            if (double.IsFinite(f0) && double.IsFinite(f1))
            {
                if (f0 == 0) AddRoot(roots, x0);
                else if (Math.Sign(f0) != Math.Sign(f1) && f1 != 0)
                {
                    var r = Brent(f, x0, x1);
                    // Reject poles: a genuine root has a small function value.
                    if (r is { } root && Math.Abs(f(root)) < 1e-6 * Math.Max(1, Math.Max(Math.Abs(f0), Math.Abs(f1)))) AddRoot(roots, root);
                }
                else if (i >= 2 && Math.Abs(f0) < prevAbs && Math.Abs(f0) < Math.Abs(f1) && Math.Abs(f0) < 1e-2)
                {
                    // Possible double root (touching zero): minimise |f| locally.
                    var m = GoldenMin(t => Math.Abs(f(t)), x0 - h, x1);
                    if (Math.Abs(f(m)) < 1e-10) AddRoot(roots, m);
                }
            }
            prevAbs = Math.Abs(f0);
            x0 = x1; f0 = f1;
        }
        if (f0 == 0) AddRoot(roots, x0);
        return roots;
    }

    private static void AddRoot(List<double> roots, double r)
    {
        if (Math.Abs(r) < 1e-14) r = 0;
        if (roots.Count == 0 || Math.Abs(roots[^1] - r) > 1e-9 * Math.Max(1, Math.Abs(r))) roots.Add(r);
    }

    public static double? Brent(Func<double, double> f, double a, double b, double tol = 1e-15)
    {
        double fa = f(a), fb = f(b);
        if (Math.Sign(fa) == Math.Sign(fb)) return null;
        double c = a, fc = fa, d = b - a, e = d;
        for (int iter = 0; iter < 200; iter++)
        {
            if (Math.Sign(fb) == Math.Sign(fc)) { c = a; fc = fa; d = e = b - a; }
            if (Math.Abs(fc) < Math.Abs(fb)) { a = b; b = c; c = a; fa = fb; fb = fc; fc = fa; }
            double tol1 = 2 * 1e-16 * Math.Abs(b) + 0.5 * tol;
            double xm = 0.5 * (c - b);
            if (Math.Abs(xm) <= tol1 || fb == 0) return b;
            if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
            {
                double s = fb / fa, p, q;
                if (a == c) { p = 2 * xm * s; q = 1 - s; }
                else
                {
                    double qq = fa / fc, r = fb / fc;
                    p = s * (2 * xm * qq * (qq - r) - (b - a) * (r - 1));
                    q = (qq - 1) * (r - 1) * (s - 1);
                }
                if (p > 0) q = -q;
                p = Math.Abs(p);
                if (2 * p < Math.Min(3 * xm * q - Math.Abs(tol1 * q), Math.Abs(e * q))) { e = d; d = p / q; }
                else { d = xm; e = d; }
            }
            else { d = xm; e = d; }
            a = b; fa = fb;
            b += Math.Abs(d) > tol1 ? d : Math.Sign(xm) * tol1;
            fb = f(b);
        }
        return b;
    }

    private static double GoldenMin(Func<double, double> f, double a, double b)
    {
        const double g = 0.6180339887498949;
        double c = b - g * (b - a), d = a + g * (b - a);
        for (int i = 0; i < 100; i++)
        {
            if (f(c) < f(d)) b = d; else a = c;
            c = b - g * (b - a);
            d = a + g * (b - a);
        }
        return (a + b) / 2;
    }

    // ---- quadrature -------------------------------------------------------------------------------

    private static readonly double[] GkNodes =
    [
        0.991455371120812639, 0.949107912342758525, 0.864864423359769073, 0.741531185599394440,
        0.586087235467691130, 0.405845151377397167, 0.207784955007898468, 0.000000000000000000,
    ];
    private static readonly double[] GkWeights =
    [
        0.022935322010529225, 0.063092092629978553, 0.104790010322250184, 0.140653259715525919,
        0.169004726639267903, 0.190350578064785410, 0.204432940075298892, 0.209482141084727828,
    ];
    private static readonly double[] GWeights =
    [
        0.129484966168869693, 0.279705391489276668, 0.381830050505118945, 0.417959183673469388,
    ];

    /// <summary>Adaptive Gauss-Kronrod (7-15) integration; infinite limits are handled by a change of variables.</summary>
    public static (double Value, double Error) Integrate(Func<double, double> f, double a, double b, double tol = 1e-12)
    {
        if (a == b) return (0, 0);
        if (a > b) { var (v, e) = Integrate(f, b, a, tol); return (-v, e); }
        if (double.IsInfinity(a) || double.IsInfinity(b))
        {
            if (double.IsNegativeInfinity(a) && double.IsPositiveInfinity(b))
            {
                var (v1, e1) = Integrate(f, double.NegativeInfinity, 0, tol);
                var (v2, e2) = Integrate(f, 0, double.PositiveInfinity, tol);
                return (v1 + v2, e1 + e2);
            }
            if (double.IsPositiveInfinity(b))
                return Integrate(t => { double x = a + t / (1 - t); return f(x) / ((1 - t) * (1 - t)); }, 0, 1, tol);
            return Integrate(t => { double x = b - (1 - t) / t; return f(x) / (t * t); }, 0, 1, tol);
        }
        double total = 0, err = 0;
        var stack = new Stack<(double, double, int)>();
        stack.Push((a, b, 0));
        int evals = 0;
        while (stack.Count > 0)
        {
            var (lo, hi, depth) = stack.Pop();
            var (k, g) = Gk15(f, lo, hi);
            evals++;
            double e = Math.Abs(k - g);
            if (e <= Math.Max(tol * Math.Abs(k), 1e-15 * (hi - lo)) || depth > 40 || evals > 20000 || double.IsNaN(k))
            {
                total += k; err += e;
            }
            else
            {
                double mid = (lo + hi) / 2;
                stack.Push((lo, mid, depth + 1));
                stack.Push((mid, hi, depth + 1));
            }
        }
        return (total, err);
    }

    private static (double Kronrod, double Gauss) Gk15(Func<double, double> f, double a, double b)
    {
        double c = (a + b) / 2, h = (b - a) / 2;
        double fc = Safe(f, c);
        double k = fc * GkWeights[7], g = fc * GWeights[3];
        for (int i = 0; i < 7; i++)
        {
            double dx = h * GkNodes[i];
            double f1 = Safe(f, c - dx), f2 = Safe(f, c + dx);
            k += GkWeights[i] * (f1 + f2);
            if (i % 2 == 1) g += GWeights[i / 2] * (f1 + f2);
        }
        return (k * h, g * h);
    }

    /// <summary>
    /// Tanh-sinh (double exponential) quadrature on a finite interval. Its nodes cluster doubly exponentially
    /// at the endpoints, so integrable endpoint singularities (1/sqrt(x), log(x)) converge to full precision.
    /// </summary>
    public static (double Value, double Error) TanhSinh(Func<double, double> f, double a, double b, double tol = 1e-13)
    {
        if (a == b) return (0, 0);
        double c = (a + b) / 2, half = (b - a) / 2;
        // Evaluate at c ± half·(1 − δ) with δ computed directly, so nodes very close to an endpoint stay distinct from it.
        double Node(double t, out double weight)
        {
            double u = Math.PI / 2 * Math.Sinh(t);
            double cu = Math.Cosh(u);
            weight = Math.PI / 2 * Math.Cosh(t) / (cu * cu);
            return 1 / (Math.Exp(2 * Math.Abs(u)) + 1) * 2; // 1 − tanh|u|
        }
        double h = 1.0, previous = double.NaN, estimate = 0;
        double Sum(double step, bool oddOnly)
        {
            double s = 0;
            for (int k = oddOnly ? 1 : 0; ; k += oddOnly ? 2 : 1)
            {
                double t = k * step;
                double delta = Node(t, out var w);
                if (w * half < 1e-300 || delta == 0) break;
                if (k == 0)
                {
                    s += w * Safe(f, c);
                    continue;
                }
                double xl = a + half * delta, xr = b - half * delta;
                s += w * (Safe(f, xl) + Safe(f, xr));
                if (t > 6) break;
            }
            return s;
        }
        double sum = Sum(h, false);
        estimate = sum * h * half;
        for (int level = 0; level < 10; level++)
        {
            sum += Sum(h / 2, true);
            h /= 2;
            previous = estimate;
            estimate = sum * h * half;
            if (level > 2 && Math.Abs(estimate - previous) <= tol * Math.Max(1, Math.Abs(estimate))) break;
        }
        return (estimate, Math.Abs(estimate - previous));
    }

    /// <summary>Integrable endpoint singularities are never sampled by GK nodes, but guard against NaN anyway.</summary>
    private static double Safe(Func<double, double> f, double x)
    {
        var v = f(x);
        return double.IsFinite(v) ? v : 0;
    }

    /// <summary>Central-difference derivative estimate.</summary>
    public static double Derivative(Func<double, double> f, double x)
    {
        double h = 1e-5 * Math.Max(1, Math.Abs(x));
        return (f(x - 2 * h) - 8 * f(x - h) + 8 * f(x + h) - f(x + 2 * h)) / (12 * h);
    }

    /// <summary>Newton's method for systems with a finite-difference Jacobian.</summary>
    public static double[]? NewtonSystem(Func<double[], double>[] fs, double[] x0, int maxIter = 100)
    {
        int n = x0.Length;
        var x = (double[])x0.Clone();
        for (int iter = 0; iter < maxIter; iter++)
        {
            var fx = fs.Select(f => f(x)).ToArray();
            if (fx.Any(v => !double.IsFinite(v))) return null;
            double norm = fx.Max(Math.Abs);
            if (norm < 1e-14) return x;
            var J = new double[n, n + 1];
            for (int j = 0; j < n; j++)
            {
                double h = 1e-7 * Math.Max(1, Math.Abs(x[j]));
                var xp = (double[])x.Clone(); xp[j] += h;
                for (int i = 0; i < n; i++) J[i, j] = (fs[i](xp) - fx[i]) / h;
            }
            for (int i = 0; i < n; i++) J[i, n] = -fx[i];
            var dx = SolveDense(J, n);
            if (dx is null) return null;
            double step = 1;
            for (int i = 0; i < n; i++) x[i] += step * dx[i];
            if (dx.Max(Math.Abs) < 1e-15 * Math.Max(1, x.Max(Math.Abs))) return x;
        }
        var final = fs.Select(f => f(x)).Max(Math.Abs);
        return final < 1e-9 ? x : null;
    }

    public static double[]? SolveDense(double[,] aug, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int i = c + 1; i < n; i++) if (Math.Abs(aug[i, c]) > Math.Abs(aug[p, c])) p = i;
            if (Math.Abs(aug[p, c]) < 1e-300) return null;
            for (int j = 0; j <= n; j++) (aug[c, j], aug[p, j]) = (aug[p, j], aug[c, j]);
            for (int i = c + 1; i < n; i++)
            {
                double f = aug[i, c] / aug[c, c];
                for (int j = c; j <= n; j++) aug[i, j] -= f * aug[c, j];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = aug[i, n];
            for (int j = i + 1; j < n; j++) s -= aug[i, j] * x[j];
            x[i] = s / aug[i, i];
        }
        return x;
    }
}
