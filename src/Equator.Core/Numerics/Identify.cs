using System.Numerics;
using Equator.Core.Algebra;

namespace Equator.Core.Numerics;

/// <summary>
/// Recognition of exact constants from high-precision approximations: integer relations by PSLQ
/// (Ferguson–Bailey), used to identify rationals, algebraic numbers of small degree, and rational
/// combinations of π, e, logarithms and square roots. Every candidate is re-evaluated and must agree with
/// the input to nearly all of its digits before it is accepted.
/// </summary>
public static class Identify
{
    /// <summary>Integer vector m (not all zero) with Σ mᵢ xᵢ ≈ 0 and |mᵢ| ≤ maxCoeff, or null.</summary>
    public static BigInteger[]? Pslq(IReadOnlyList<BigFloat> x, int bits, BigInteger maxCoeff, int maxIterations = 2000)
    {
        int n = x.Count;
        if (n < 2) return null;
        int wp = bits;
        var zero = BigFloat.Zero(wp);
        var one = BigFloat.FromInteger(1, wp);
        var gamma = BigFloat.Sqrt(BigFloat.Divide(BigFloat.FromInteger(4, wp), BigFloat.FromInteger(3, wp), wp), wp);

        // Normalised partial sums s_k = sqrt(Σ_{j≥k} x_j²) and y = x / s_0.
        var xs = x.Select(v => v.WithPrecision(wp)).ToArray();
        if (xs.Any(v => v.IsZero))
        {
            // A zero entry is itself a relation.
            var rel = new BigInteger[n];
            rel[Array.FindIndex(xs, v => v.IsZero)] = 1;
            return rel;
        }
        var s = new BigFloat[n];
        var acc = zero;
        for (int k = n - 1; k >= 0; k--) { acc += xs[k] * xs[k]; s[k] = BigFloat.Sqrt(acc, wp); }
        var t0 = s[0];
        var y = xs.Select(v => BigFloat.Divide(v, t0, wp)).ToArray();
        for (int k = 0; k < n; k++) s[k] = BigFloat.Divide(s[k], t0, wp);

        var H = new BigFloat[n, n - 1];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n - 1; j++)
            {
                if (j > i) H[i, j] = zero;
                else if (j == i) H[i, j] = BigFloat.Divide(s[i + 1], s[i], wp);
                else H[i, j] = -BigFloat.Divide(y[i] * y[j], s[j] * s[j + 1], wp);
            }
        var A = new BigInteger[n, n];
        var B = new BigInteger[n, n];
        for (int i = 0; i < n; i++) { A[i, i] = 1; B[i, i] = 1; }

        void Reduce(int fromRow)
        {
            for (int i = Math.Max(fromRow, 1); i < n; i++)
                for (int j = Math.Min(i - 1, n - 2); j >= 0; j--)
                {
                    if (H[j, j].IsZero) continue;
                    var t = BigFloat.Divide(H[i, j], H[j, j], wp).Round();
                    if (t.IsZero) continue;
                    var tf = BigFloat.FromInteger(t, wp);
                    y[j] += tf * y[i];
                    for (int k = 0; k <= j; k++) H[i, k] -= tf * H[j, k];
                    for (int k = 0; k < n; k++) { A[i, k] -= t * A[j, k]; B[k, j] += t * B[k, i]; }
                }
        }
        Reduce(1);

        var threshold = BigFloat.FromInteger(1, wp).Scale(-(bits - 24));
        for (int iter = 0; iter < maxIterations; iter++)
        {
            Engine.Interrupt.Check();
            // Choose m maximising γ^(i+1) |H_ii|.
            int m = 0;
            BigFloat best = zero, g = gamma;
            for (int i = 0; i < n - 1; i++)
            {
                var v = (g * H[i, i]).Abs();
                if (v.CompareTo(best) > 0) { best = v; m = i; }
                g *= gamma;
            }
            // Swap rows m and m + 1.
            (y[m], y[m + 1]) = (y[m + 1], y[m]);
            for (int k = 0; k < n; k++)
            {
                (A[m, k], A[m + 1, k]) = (A[m + 1, k], A[m, k]);
                (B[k, m], B[k, m + 1]) = (B[k, m + 1], B[k, m]);
            }
            for (int k = 0; k < n - 1; k++) (H[m, k], H[m + 1, k]) = (H[m + 1, k], H[m, k]);
            // Restore the lower-trapezoidal shape.
            if (m < n - 2)
            {
                var a = H[m, m]; var b = H[m, m + 1];
                var r = BigFloat.Sqrt(a * a + b * b, wp);
                if (!r.IsZero)
                {
                    var c1 = BigFloat.Divide(a, r, wp); var c2 = BigFloat.Divide(b, r, wp);
                    for (int i = m; i < n; i++)
                    {
                        var h3 = H[i, m]; var h4 = H[i, m + 1];
                        H[i, m] = c1 * h3 + c2 * h4;
                        H[i, m + 1] = c1 * h4 - c2 * h3;
                    }
                }
            }
            Reduce(m + 1);

            // A tiny y_j means column j of B is a relation.
            for (int j = 0; j < n; j++)
            {
                if (y[j].Abs().CompareTo(threshold) >= 0) continue;
                var rel = new BigInteger[n];
                for (int k = 0; k < n; k++) rel[k] = B[k, j];
                if (rel.All(v => BigInteger.Abs(v) <= maxCoeff) && rel.Any(v => !v.IsZero)) return rel;
                return null;
            }
            // Give up once the coefficients exceed the bound.
            BigInteger maxA = 0;
            foreach (var v in A) maxA = BigInteger.Max(maxA, BigInteger.Abs(v));
            if (maxA > maxCoeff * maxCoeff * 1000) return null;
        }
        return null;
    }

    /// <summary>
    /// Tries to identify v (given with the stated number of reliable decimal digits) as a simple exact
    /// constant. Returns null when nothing convincing is found.
    /// </summary>
    public static Expr? Constant(BigFloat v, int digits)
    {
        if (digits < 15) return null;
        int bits = BigFloat.DigitsToBits(digits);
        int checkDigits = digits - 4;
        if (v.IsZero || v.Log2Magnitude < -bits + 8) return Expr.Zero;

        Expr? Accept(Expr candidate)
        {
            try
            {
                var c = BigEval.Eval(candidate, bits + 16);
                if (!c.IsReal && !c.Im.IsZero && c.Im.Log2Magnitude > -bits + 16) return null;
                var diff = (c.Re - v).Abs();
                var scale = Math.Max(0, v.Log2Magnitude);
                if (diff.IsZero || diff.Log2Magnitude < scale - (int)(checkDigits * 3.32)) return candidate;
            }
            catch (MathException) { }
            catch (DivideByZeroException) { }
            return null;
        }

        var limit = BigInteger.Pow(10, Math.Max(3, digits / 4));
        BigFloat C(Expr e) => BigEval.Eval(e, bits + 32).Re;
        Expr Combine(BigInteger[] rel, Expr[] basis)
        {
            // rel·[v, basis...] = 0  ⇒  v = −Σ rel_i basis_i / rel_0
            var sum = Expr.Plus(basis.Select((b, i) => new Num(rel[i + 1]) * b));
            return -sum / new Num(rel[0]);
        }

        // Linear combinations with a few classic constants.
        Expr[][] bases =
        [
            [Expr.One],
            [Expr.One, Expr.Pi],
            [Expr.Pi * Expr.Pi],
            [Expr.One, Expr.Sqrt(2)], [Expr.One, Expr.Sqrt(3)], [Expr.One, Expr.Sqrt(5)],
            [Expr.Pi * Expr.Sqrt(2)], [Expr.Pi * Expr.Sqrt(3)], [Expr.Pi / Expr.Sqrt(3)],
            [Expr.E], [Expr.One, Expr.E], [Expr.One / Expr.E],
            [Expr.One, Expr.Call("log", 2)], [Expr.Call("log", 2), Expr.Call("log", 3)],
            [Expr.One, Expr.Pi, Expr.Call("log", 2)],
            [Expr.One, Expr.Pi, Expr.Sqrt(2)], [Expr.One, Expr.Pi, Expr.Sqrt(3)], [Expr.One, Expr.E, Expr.Pi],
            [Expr.One, Expr.Sqrt(2), Expr.Sqrt(3)], [Expr.One, Expr.Call("zeta", 3)],
        ];
        foreach (var basis in bases)
        {
            var vec = new List<BigFloat> { v };
            vec.AddRange(basis.Select(C));
            var rel = Pslq(vec, bits - 8, limit);
            if (rel is null || rel[0].IsZero) continue;
            if (Accept(Combine(rel, basis)) is { } found) return Algebra.Solver.Tidy(found);
        }

        // Algebraic numbers of degree 2..4: integer polynomial with v as a root.
        for (int degree = 2; degree <= 4; degree++)
        {
            var powers = new List<BigFloat>();
            var p = BigFloat.FromInteger(1, bits + 32);
            for (int k = 0; k <= degree; k++) { powers.Add(p); p *= v.WithPrecision(bits + 32); }
            var rel = Pslq(powers, bits - 8, BigInteger.Pow(10, Math.Max(2, digits / (2 * degree + 2))));
            if (rel is null || rel[degree].IsZero) continue;
            var x = new Sym("\u0001id");
            var poly = Expr.Plus(rel.Select((c, k) => new Num(c) * Expr.Power(x, k)));
            var roots = Algebra.Solver.PolynomialRoots(poly, x);
            if (roots is null) continue;
            foreach (var r in roots)
            {
                if (r.ContainsAny(z => z is Real)) continue;
                if (Accept(r) is { } found) return found;
            }
        }

        // Exponentials of simple values: v = ± e^q or ± 2^a 3^b 5^c.
        if (v.Sign > 0)
        {
            var lv = BigMath.Log(v, bits + 32);
            Expr[] logBasis = [Expr.One, Expr.Call("log", 2), Expr.Call("log", 3), Expr.Call("log", 5)];
            var vec = new List<BigFloat> { lv };
            vec.AddRange(logBasis.Select(C));
            var rel = Pslq(vec, bits - 8, BigInteger.Pow(10, Math.Max(2, digits / 8)));
            if (rel is not null && !rel[0].IsZero)
            {
                // log v = −(r₁ + r₂ log 2 + r₃ log 3 + r₄ log 5)/r₀  ⇒  v = e^(−r₁/r₀) 2^(−r₂/r₀) 3^(−r₃/r₀) 5^(−r₄/r₀)
                Expr Exponent(int i) => new Num(new BigRational(-rel[i], rel[0]));
                var candidate = Expr.Times(Expr.Exp(Exponent(1)), Expr.Power(2, Exponent(2)), Expr.Power(3, Exponent(3)), Expr.Power(5, Exponent(4)));
                if (Accept(candidate) is { } found) return found;
            }
        }
        return null;
    }
}
