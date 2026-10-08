using System.Numerics;

namespace Equator.Core.Algebra;

/// <summary>
/// Complete factorisation of multivariate polynomials over Q. Univariate square-free parts go to
/// <see cref="ZFactor"/>. Multivariate ones are evaluated at an integer point in all variables but one, the
/// univariate image is factored, and image factors are lifted back by Hensel lifting in the other variables
/// (with Wang's trick of imposing the known leading coefficient on every factor). Subsets of image factors
/// are tried in increasing size, so spurious image splittings are recombined.
/// </summary>
public static class PolyFactor
{
    /// <summary>p = unit · Π fᵢ^mᵢ with each fᵢ irreducible, integer, primitive and positive in the lex order.</summary>
    public static (BigRational Unit, List<(MPoly Factor, int Multiplicity)> Factors) Factor(MPoly p)
    {
        if (p.IsZero) return (BigRational.Zero, []);
        var factors = new List<(MPoly, int)>();
        FactorInto(p.Normalized(), 1, factors);
        // Merge equal factors.
        var merged = new List<(MPoly F, int M)>();
        foreach (var (f, m) in factors)
        {
            int i = merged.FindIndex(t => t.F.Equals(f));
            if (i >= 0) merged[i] = (f, merged[i].M + m);
            else merged.Add((f, m));
        }
        var product = MPoly.Constant(p.NVars, BigRational.One);
        foreach (var (f, m) in merged) product *= f.Pow(m);
        var unit = p.Lead.C / product.Lead.C;
        return (unit, merged);
    }

    /// <summary>Factors a normalised (integer, primitive, positive) polynomial.</summary>
    private static void FactorInto(MPoly p, int mult, List<(MPoly, int)> output)
    {
        Engine.Interrupt.Check();
        if (p.IsConstant) return;
        int n = p.NVars;

        // Monomial content: x^k y^j ...
        var minExp = new int[n];
        for (int i = 0; i < n; i++) minExp[i] = p.Terms.Min(t => t.M[i]);
        if (minExp.Any(e => e > 0))
        {
            for (int i = 0; i < n; i++)
                if (minExp[i] > 0) output.Add((MPoly.Var(n, i), mult * minExp[i]));
            p = MPoly.TryDivide(p, MPoly.FromTerms(n, [(new Mono(minExp), BigRational.One)]))!.Normalized();
            if (p.IsConstant) return;
        }

        var vars = p.Variables.ToList();
        int v = vars.OrderBy(w => p.Degree(w)).ThenBy(w => p.TermCount).First();

        // Content with respect to v is a polynomial in the other variables.
        var content = PolyGcd.Content(p, v);
        if (!content.IsConstant)
        {
            FactorInto(content.Normalized(), mult, output);
            p = MPoly.TryDivide(p, content)!.Normalized();
        }

        foreach (var (sqf, m) in PolyGcd.SquareFree(p, v))
            foreach (var f in FactorSquareFree(sqf.Normalized(), v))
                output.Add((f.Normalized(), mult * m));
    }

    /// <summary>Irreducible factors of a square-free polynomial that is primitive in v.</summary>
    private static List<MPoly> FactorSquareFree(MPoly f, int v)
    {
        if (f.Degree(v) <= 1) return [f];
        var others = f.Variables.Where(w => w != v).ToList();
        if (others.Count == 0)
        {
            var coeffs = f.UnivariateCoefficients(v).Select(c => c.Num).ToArray();
            return ZFactor.FactorSquareFree(coeffs)
                .Select(c => MPoly.FromUnivariate(f.NVars, v, c.Select(b => new BigRational(b)).ToList()))
                .ToList();
        }
        return Multivariate(f, v, others);
    }

    // ---- multivariate ----------------------------------------------------------------------------------

    private sealed record Image(BigRational[] Point, List<BigInteger[]> Factors);

    private static List<MPoly> Multivariate(MPoly F, int x, List<int> ys)
    {
        var image = ChooseImage(F, x, ys);
        if (image is null || image.Factors.Count <= 1) return [F];

        var result = new List<MPoly>();
        var remaining = image.Factors.ToList();
        var current = F;
        int s = 1;
        while (2 * s <= remaining.Count)
        {
            bool found = false;
            foreach (var subset in Subsets(remaining.Count, s))
            {
                Engine.Interrupt.Check();
                var g0 = subset.Aggregate(new BigInteger[] { 1 }, (acc, i) => ZFactor.Mul(acc, remaining[i]));
                var h0 = Enumerable.Range(0, remaining.Count).Where(i => !subset.Contains(i))
                    .Aggregate(new BigInteger[] { 1 }, (acc, i) => ZFactor.Mul(acc, remaining[i]));
                var lifted = Lift2(current, g0, h0, x, ys, image.Point);
                if (lifted is null) continue;
                result.Add(lifted.Value.G);
                current = lifted.Value.H;
                foreach (var i in subset.OrderByDescending(i => i)) remaining.RemoveAt(i);
                found = true;
                break;
            }
            if (!found) s++;
            if (s > 6) break; // give up on recombining very large subsets: the rest stays unfactored
        }
        result.Add(current);
        return result;
    }

    /// <summary>
    /// Picks an evaluation point for ys keeping deg_x and square-freeness, and factors the image; prefers the
    /// image with fewest factors among a few candidates.
    /// </summary>
    private static Image? ChooseImage(MPoly F, int x, List<int> ys)
    {
        var L = F.LeadingCoefficientIn(x);
        int degX = F.Degree(x);
        var rng = new Random(7);
        Image? best = null;
        int good = 0;
        for (int attempt = 0; attempt < 40 && good < 3; attempt++)
        {
            Engine.Interrupt.Check();
            int range = 2 + attempt / 4;
            var point = new BigRational[F.NVars];
            for (int i = 0; i < point.Length; i++) point[i] = BigRational.Zero;
            foreach (var y in ys) point[y] = attempt == 0 ? new BigRational(y % 3 + 1) : new BigRational(rng.Next(-range, range + 1));

            var La = L;
            foreach (var y in ys) La = La.Evaluate(y, point[y]);
            if (La.IsZero) continue;
            var f = F;
            foreach (var y in ys) f = f.Evaluate(y, point[y]);
            if (f.Degree(x) != degX) continue;
            var q = f.ToQPoly(x);
            if (QPoly.Gcd(q, q.Derivative()).Degree > 0) continue;
            good++;
            var (_, prim) = q.ContentPrimitive();
            var factors = ZFactor.FactorSquareFree(prim.Coeffs.Select(c => c.Num).ToArray());
            if (best is null || factors.Count < best.Factors.Count) best = new Image(point, factors);
            if (factors.Count == 1) break;
        }
        return best;
    }

    /// <summary>
    /// Hensel lifting of F(x, y) ≡ g0(x) h0(x) at y = a to a factorisation F = G H, or null when the image
    /// factorisation does not lift (g0 is not the image of a true factor).
    /// </summary>
    private static (MPoly G, MPoly H)? Lift2(MPoly F, BigInteger[] g0i, BigInteger[] h0i, int x, List<int> ys, BigRational[] a)
    {
        int n = F.NVars;
        // Shift so that the evaluation point is the origin.
        var Fs = F;
        foreach (var y in ys) Fs = Fs.Shift(y, a[y]);
        var L = Fs.LeadingCoefficientIn(x);
        var c0 = L;
        foreach (var y in ys) c0 = c0.Evaluate(y, BigRational.Zero);
        var lc0 = c0.ConstantValue;

        var g0q = new QPoly(g0i.Select(b => new BigRational(b)));
        var h0q = new QPoly(h0i.Select(b => new BigRational(b)));
        // Impose the leading coefficient L(0) on both images.
        g0q = (lc0 / g0q.Lead) * g0q;
        h0q = (lc0 / h0q.Lead) * h0q;
        int dg = g0q.Degree, dh = h0q.Degree;

        var (gcd, s, t) = QPoly.ExtendedGcd(g0q, h0q);
        if (gcd.Degree != 0) return null;
        s = (BigRational.One / gcd[0]) * s;
        t = (BigRational.One / gcd[0]) * t;

        var Fp = L * Fs;
        var lcShift = L - MPoly.Constant(n, lc0);
        var G = MPoly.FromUnivariate(n, x, g0q.Coeffs) + lcShift * MPoly.Var(n, x, dg);
        var H = MPoly.FromUnivariate(n, x, h0q.Coeffs) + lcShift * MPoly.Var(n, x, dh);

        int D = Fp.Terms.Max(term => ys.Sum(y => term.M[y]));
        for (int k = 1; k <= D; k++)
        {
            Engine.Interrupt.Check();
            var E = Fp - G * H;
            if (E.IsZero) break;
            var Ek = E.HomogeneousPart(ys, k);
            if (Ek.IsZero) continue;
            // Group by the y-monomial: Ek = Σ e_m(x) m.
            var groups = new Dictionary<Mono, Dictionary<int, BigRational>>();
            foreach (var (m, c) in Ek.Terms)
            {
                var key = m.With(x, 0);
                if (!groups.TryGetValue(key, out var coeffs)) groups[key] = coeffs = new();
                coeffs[m[x]] = c;
            }
            foreach (var (mono, coeffs) in groups)
            {
                int deg = coeffs.Keys.Max();
                var e = new QPoly(Enumerable.Range(0, deg + 1).Select(i => coeffs.GetValueOrDefault(i, BigRational.Zero)));
                // A h0 + B g0 = e with deg A < dg, deg B < dh.
                var A = (t * e).DivRem(g0q).Remainder;
                var (B, rem) = (e - A * h0q).DivRem(g0q);
                if (!rem.IsZero || B.Degree >= dh) return null;
                G += MPoly.FromUnivariate(n, x, A.Coeffs).MulMono(mono, BigRational.One);
                H += MPoly.FromUnivariate(n, x, B.Coeffs).MulMono(mono, BigRational.One);
            }
        }
        if (!(Fp - G * H).IsZero) return null;

        var Gp = PolyGcd.PrimitivePart(G, x);
        var Hs = MPoly.TryDivide(Fs, Gp);
        if (Hs is null) return null;
        foreach (var y in ys)
        {
            Gp = Gp.Shift(y, -a[y]);
            Hs = Hs.Shift(y, -a[y]);
        }
        return (Gp.Normalized(), Hs.Normalized());
    }

    private static IEnumerable<int[]> Subsets(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            int i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (int j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}
