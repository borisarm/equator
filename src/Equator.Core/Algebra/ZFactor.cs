using System.Numerics;

namespace Equator.Core.Algebra;

/// <summary>
/// Factorisation of univariate polynomials over the integers (Zassenhaus): factor modulo a small prime p with
/// the Cantor–Zassenhaus algorithm, lift the modular factors to p^k with quadratic Hensel lifting, then
/// recombine them into true factors by trial division. Cyclotomic binomials x^n ± 1 are split directly.
/// Polynomials are coefficient arrays with index = power.
/// </summary>
public static class ZFactor
{
    /// <summary>Irreducible factors (primitive, positive leading coefficient) of a square-free primitive integer polynomial.</summary>
    public static List<BigInteger[]> FactorSquareFree(BigInteger[] f)
    {
        f = Trim(f);
        int n = f.Length - 1;
        if (n <= 1) return [f];
        // Remove the factor x.
        if (f[0].IsZero)
        {
            var rest = FactorSquareFree(f[1..]);
            rest.Insert(0, [BigInteger.Zero, BigInteger.One]);
            return rest;
        }
        if (Binomial(f) is { } cyclo) return cyclo;
        if (n == 2 && IsIrreducibleQuadratic(f)) return [f];
        return Zassenhaus(f);
    }

    private static bool IsIrreducibleQuadratic(BigInteger[] f)
    {
        var disc = f[1] * f[1] - 4 * f[2] * f[0];
        if (disc.Sign < 0) return true;
        return !IntMath.TryExactRoot(disc, 2, out _);
    }

    // ---- cyclotomic binomials ------------------------------------------------------------------------

    /// <summary>x^n − 1 = Π_{d | n} Φ_d and x^n + 1 = Π_{d | 2n, d ∤ n} Φ_d.</summary>
    private static List<BigInteger[]>? Binomial(BigInteger[] f)
    {
        int n = f.Length - 1;
        for (int i = 1; i < n; i++) if (!f[i].IsZero) return null;
        if (f[n] != 1 || BigInteger.Abs(f[0]) != 1 || n > 400) return null;
        bool minus = f[0] == -1;
        var ds = new List<int>();
        if (minus) { for (int d = 1; d <= n; d++) if (n % d == 0) ds.Add(d); }
        else { for (int d = 1; d <= 2 * n; d++) if (2 * n % d == 0 && n % d != 0) ds.Add(d); }
        return ds.Select(Cyclotomic).ToList();
    }

    private static readonly Dictionary<int, BigInteger[]> CyclotomicCache = new();

    public static BigInteger[] Cyclotomic(int n)
    {
        lock (CyclotomicCache)
        {
            if (CyclotomicCache.TryGetValue(n, out var c)) return c;
        }
        // Φ_n = (x^n − 1) / Π_{d | n, d < n} Φ_d
        var num = new BigInteger[n + 1];
        num[0] = -1; num[n] = 1;
        var q = new QPoly(num.Select(b => new BigRational(b)));
        for (int d = 1; d < n; d++)
            if (n % d == 0) q = q.DivRem(new QPoly(Cyclotomic(d).Select(b => new BigRational(b)))).Quotient;
        var result = q.Coeffs.Select(r => r.Num).ToArray();
        lock (CyclotomicCache) CyclotomicCache[n] = result;
        return result;
    }

    // ---- Zassenhaus -------------------------------------------------------------------------------------

    private static readonly int[] Primes =
    [
        3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97, 101, 103, 107, 109,
        113, 127, 131, 137, 139, 149, 151, 157, 163, 167, 173, 179, 181, 191, 193, 197, 199, 211, 223, 227, 229, 233,
        239, 241, 251, 257, 263, 269, 271, 277, 281, 283, 293, 307, 311, 313, 317, 331, 337, 347, 349, 353, 359, 367,
    ];

    private static List<BigInteger[]> Zassenhaus(BigInteger[] f)
    {
        int n = f.Length - 1;
        var lc = f[n];

        // Choose the prime giving the fewest modular factors among the first few suitable ones.
        long bestP = 0;
        List<(long[] G, int D)>? bestDdf = null;
        int bestCount = int.MaxValue, tried = 0;
        foreach (var p in Primes)
        {
            if ((lc % p).IsZero) continue;
            var fp = ModP.Reduce(f, p);
            if (fp.Length - 1 != n) continue;
            if (ModP.Gcd(fp, ModP.Derivative(fp, p), p).Length > 1) continue; // not square-free mod p
            var monic = ModP.Monic(fp, p);
            var ddf = ModP.DistinctDegree(monic, p);
            int count = ddf.Sum(t => (t.G.Length - 1) / t.D);
            if (count < bestCount) { bestCount = count; bestP = p; bestDdf = ddf; }
            if (count == 1 || ++tried >= 6) break;
        }
        if (bestDdf is null) return [f];
        if (bestCount == 1) return [f];

        var modFactors = new List<long[]>();
        var rng = new Random(12345);
        foreach (var (g, d) in bestDdf) modFactors.AddRange(ModP.EqualDegree(g, d, bestP, rng));

        // Modulus large enough to recover any factor: Mignotte bound times |lc|.
        var norm = Sqrt(f.Aggregate(BigInteger.Zero, (s, c) => s + c * c)) + 1;
        var bound = 2 * BigInteger.Abs(lc) * BigInteger.Pow(2, n) * norm + 1;
        int k = 1;
        var M = (BigInteger)bestP;
        while (M <= bound) { M *= bestP; k++; }

        var lifted = HenselLiftAll(f, modFactors, bestP, k);
        return Recombine(f, lifted, M);
    }

    private static BigInteger Sqrt(BigInteger v)
    {
        if (v.Sign <= 0) return 0;
        var x = (BigInteger)Math.Sqrt((double)v);
        while (x * x > v) x--;
        while ((x + 1) * (x + 1) <= v) x++;
        return x;
    }

    /// <summary>Lifts monic factors of f/lc(f) mod p to monic factors mod p^k.</summary>
    private static List<BigInteger[]> HenselLiftAll(BigInteger[] f, List<long[]> factors, long p, int k)
    {
        var pk = BigInteger.Pow(p, k);
        var lcInv = BigInteger.ModPow(Mod(f[^1], pk), Phi(p, k) - 1, pk);
        var fm = Reduce(f.Select(c => c * lcInv).ToArray(), pk);
        return LiftTree(fm, factors.Select(u => u.Select(c => (BigInteger)c).ToArray()).ToList(), p, k);
    }

    private static BigInteger Phi(long p, int k) => BigInteger.Pow(p, k - 1) * (p - 1);

    private static List<BigInteger[]> LiftTree(BigInteger[] f, List<BigInteger[]> factors, long p, int k)
    {
        if (factors.Count == 1) return [f];
        int half = factors.Count / 2;
        var a = factors.Take(half).ToList();
        var b = factors.Skip(half).ToList();
        var g = a.Aggregate(new BigInteger[] { 1 }, (acc, u) => Reduce(Mul(acc, u), p));
        var h = b.Aggregate(new BigInteger[] { 1 }, (acc, u) => Reduce(Mul(acc, u), p));
        var (G, H) = HenselLift(f, g, h, p, k);
        var res = LiftTree(G, a, p, k);
        res.AddRange(LiftTree(H, b, p, k));
        return res;
    }

    /// <summary>Quadratic Hensel lifting of f ≡ g h (mod p), g and h monic and coprime mod p, to mod p^k.</summary>
    private static (BigInteger[] G, BigInteger[] H) HenselLift(BigInteger[] f, BigInteger[] g, BigInteger[] h, long p, int k)
    {
        var (_, s0, t0) = ModP.ExtendedGcd(g.Select(c => (long)c).ToArray(), h.Select(c => (long)c).ToArray(), p);
        BigInteger[] s = s0.Select(c => (BigInteger)c).ToArray(), t = t0.Select(c => (BigInteger)c).ToArray();
        BigInteger m = p;
        var target = BigInteger.Pow(p, k);
        while (m < target)
        {
            Engine.Interrupt.Check();
            var m2 = m * m;
            // von zur Gathen & Gerhard, Algorithm 15.10.
            var e = Reduce(Sub(f, Mul(g, h)), m2);
            var (q, r) = DivRemMonic(Reduce(Mul(s, e), m2), h, m2);
            var gs = Reduce(Add(g, Add(Mul(t, e), Mul(q, g))), m2);
            var hs = Reduce(Add(h, r), m2);
            var bb = Reduce(Sub(Add(Mul(s, gs), Mul(t, hs)), [1]), m2);
            var (c, d) = DivRemMonic(Reduce(Mul(s, bb), m2), hs, m2);
            s = Reduce(Sub(s, d), m2);
            t = Reduce(Sub(t, Add(Mul(t, bb), Mul(c, gs))), m2);
            g = gs; h = hs; m = m2;
        }
        return (Reduce(g, target), Reduce(h, target));
    }

    /// <summary>Zassenhaus recombination: products of subsets of lifted factors that divide f over Z.</summary>
    private static List<BigInteger[]> Recombine(BigInteger[] f, List<BigInteger[]> lifted, BigInteger M)
    {
        var result = new List<BigInteger[]>();
        var remaining = lifted.ToList();
        var F = f;
        int s = 1;
        int maxSubset = remaining.Count > 24 ? 3 : int.MaxValue;
        while (2 * s <= remaining.Count && s <= maxSubset)
        {
            bool found = false;
            foreach (var subset in Subsets(remaining.Count, s))
            {
                Engine.Interrupt.Check();
                var lcF = F[^1];
                var g = new BigInteger[] { lcF };
                foreach (var i in subset) g = Reduce(Mul(g, remaining[i]), M);
                g = Symmetric(g, M);
                // Constant-term test before the full division.
                if (!g[0].IsZero && !(lcF * F[0] % g[0]).IsZero) continue;
                g = PrimitivePart(g);
                var q = ExactDivide(F, g);
                if (q is null) continue;
                result.Add(g);
                F = q;
                foreach (var i in subset.OrderByDescending(i => i)) remaining.RemoveAt(i);
                found = true;
                break;
            }
            if (!found) s++;
        }
        if (F.Length > 1) result.Add(PrimitivePart(F));
        return result;
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

    // ---- integer polynomial helpers ---------------------------------------------------------------

    public static BigInteger[] Trim(BigInteger[] a)
    {
        int n = a.Length;
        while (n > 0 && a[n - 1].IsZero) n--;
        return n == a.Length ? a : a[..n];
    }

    private static BigInteger Mod(BigInteger a, BigInteger m)
    {
        var r = a % m;
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger[] Reduce(BigInteger[] a, BigInteger m) => Trim(a.Select(c => Mod(c, m)).ToArray());

    private static BigInteger[] Symmetric(BigInteger[] a, BigInteger m)
    {
        var half = m / 2;
        return Trim(a.Select(c => { var r = Mod(c, m); return r > half ? r - m : r; }).ToArray());
    }

    private static BigInteger[] Add(BigInteger[] a, BigInteger[] b)
    {
        var r = new BigInteger[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++) r[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        return Trim(r);
    }

    private static BigInteger[] Sub(BigInteger[] a, BigInteger[] b)
    {
        var r = new BigInteger[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++) r[i] = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
        return Trim(r);
    }

    public static BigInteger[] Mul(BigInteger[] a, BigInteger[] b)
    {
        if (a.Length == 0 || b.Length == 0) return [];
        var r = new BigInteger[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].IsZero) continue;
            for (int j = 0; j < b.Length; j++) r[i + j] += a[i] * b[j];
        }
        return Trim(r);
    }

    /// <summary>Division by a monic polynomial modulo m.</summary>
    private static (BigInteger[] Q, BigInteger[] R) DivRemMonic(BigInteger[] a, BigInteger[] b, BigInteger m)
    {
        var r = a.ToArray();
        int db = b.Length - 1;
        if (r.Length - 1 < db) return ([], Reduce(r, m));
        var q = new BigInteger[r.Length - db];
        for (int k = r.Length - 1 - db; k >= 0; k--)
        {
            var c = Mod(r[k + db], m);
            q[k] = c;
            if (c.IsZero) continue;
            for (int j = 0; j <= db; j++) r[k + j] -= c * b[j];
        }
        return (Reduce(q, m), Reduce(r.Take(db).ToArray(), m));
    }

    public static BigInteger[] PrimitivePart(BigInteger[] a)
    {
        a = Trim(a);
        BigInteger g = 0;
        foreach (var c in a) g = BigInteger.GreatestCommonDivisor(g, c);
        if (g.IsZero) return a;
        if (a[^1].Sign < 0) g = -g;
        return a.Select(c => c / g).ToArray();
    }

    /// <summary>a / b over Z when exact, else null.</summary>
    public static BigInteger[]? ExactDivide(BigInteger[] a, BigInteger[] b)
    {
        a = Trim(a); b = Trim(b);
        int db = b.Length - 1;
        if (a.Length - 1 < db) return a.Length == 0 ? a : null;
        var r = a.ToArray();
        var q = new BigInteger[a.Length - db];
        for (int k = a.Length - 1 - db; k >= 0; k--)
        {
            var c = BigInteger.DivRem(r[k + db], b[db], out var rem);
            if (!rem.IsZero) return null;
            q[k] = c;
            if (c.IsZero) continue;
            for (int j = 0; j <= db; j++) r[k + j] -= c * b[j];
        }
        for (int i = 0; i < db; i++) if (!r[i].IsZero) return null;
        return Trim(q);
    }
}

/// <summary>Dense polynomials over Z/p (p an odd prime below 2^31), coefficient index = power, trimmed.</summary>
internal static class ModP
{
    public static long Mod(long a, long p) { a %= p; return a < 0 ? a + p : a; }

    public static long Inv(long a, long p) => (long)BigInteger.ModPow(Mod(a, p), p - 2, p);

    public static long[] Trim(long[] a)
    {
        int n = a.Length;
        while (n > 0 && a[n - 1] == 0) n--;
        return n == a.Length ? a : a[..n];
    }

    public static long[] Reduce(BigInteger[] a, long p) => Trim(a.Select(c => (long)(((c % p) + p) % p)).ToArray());

    public static long[] Monic(long[] a, long p)
    {
        if (a.Length == 0) return a;
        var inv = Inv(a[^1], p);
        return a.Select(c => c * inv % p).ToArray();
    }

    public static long[] Add(long[] a, long[] b, long p)
    {
        var r = new long[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++) r[i] = ((i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0)) % p;
        return Trim(r);
    }

    public static long[] Sub(long[] a, long[] b, long p)
    {
        var r = new long[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++) r[i] = Mod((i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0), p);
        return Trim(r);
    }

    public static long[] Mul(long[] a, long[] b, long p)
    {
        if (a.Length == 0 || b.Length == 0) return [];
        var r = new long[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == 0) continue;
            for (int j = 0; j < b.Length; j++) r[i + j] = (r[i + j] + a[i] * b[j]) % p;
        }
        return Trim(r);
    }

    public static (long[] Q, long[] R) DivRem(long[] a, long[] b, long p)
    {
        if (b.Length == 0) throw new DivideByZeroException();
        var r = a.ToArray();
        int db = b.Length - 1;
        if (r.Length - 1 < db) return ([], Trim(r));
        var inv = Inv(b[^1], p);
        var q = new long[r.Length - db];
        for (int k = r.Length - 1 - db; k >= 0; k--)
        {
            var c = r[k + db] * inv % p;
            q[k] = c;
            if (c == 0) continue;
            for (int j = 0; j <= db; j++) r[k + j] = Mod(r[k + j] - c * b[j], p);
        }
        return (Trim(q), Trim(r.Take(db).ToArray()));
    }

    public static long[] Gcd(long[] a, long[] b, long p)
    {
        a = Trim(a); b = Trim(b);
        while (b.Length > 0)
        {
            var r = DivRem(a, b, p).R;
            a = b; b = r;
        }
        return Monic(a, p);
    }

    /// <summary>(g, s, t) with s a + t b = g = gcd(a, b), g monic.</summary>
    public static (long[] G, long[] S, long[] T) ExtendedGcd(long[] a, long[] b, long p)
    {
        long[] r0 = Trim(a), r1 = Trim(b), s0 = [1], s1 = [], t0 = [], t1 = [1];
        while (r1.Length > 0)
        {
            var (q, r) = DivRem(r0, r1, p);
            (r0, r1) = (r1, r);
            (s0, s1) = (s1, Sub(s0, Mul(q, s1, p), p));
            (t0, t1) = (t1, Sub(t0, Mul(q, t1, p), p));
        }
        var inv = Inv(r0[^1], p);
        return (Monic(r0, p), s0.Select(c => c * inv % p).ToArray(), t0.Select(c => c * inv % p).ToArray());
    }

    public static long[] Derivative(long[] a, long p) => Trim(a.Skip(1).Select((c, i) => c * (i + 1) % p).ToArray());

    public static long[] PowMod(long[] b, BigInteger e, long[] f, long p)
    {
        long[] result = [1];
        b = DivRem(b, f, p).R;
        while (e > 0)
        {
            if (!e.IsEven) result = DivRem(Mul(result, b, p), f, p).R;
            e >>= 1;
            if (e > 0) b = DivRem(Mul(b, b, p), f, p).R;
        }
        return result;
    }

    /// <summary>Distinct-degree factorisation of a monic square-free polynomial: (product of all degree-d factors, d).</summary>
    public static List<(long[] G, int D)> DistinctDegree(long[] f, long p)
    {
        var result = new List<(long[], int)>();
        long[] x = [0, 1];
        var h = x;
        var rest = f;
        int d = 1;
        while (rest.Length - 1 >= 2 * d)
        {
            Engine.Interrupt.Check();
            h = PowMod(h, p, rest, p);
            var g = Gcd(rest, Sub(h, x, p), p);
            if (g.Length > 1)
            {
                result.Add((g, d));
                rest = DivRem(rest, g, p).Q;
                h = DivRem(h, rest, p).R;
            }
            d++;
        }
        if (rest.Length > 1) result.Add((rest, rest.Length - 1));
        return result;
    }

    /// <summary>Splits a product of distinct monic irreducibles of degree d (Cantor–Zassenhaus).</summary>
    public static List<long[]> EqualDegree(long[] g, int d, long p, Random rng)
    {
        int n = g.Length - 1;
        if (n == d) return [g];
        var e = (BigInteger.Pow(p, d) - 1) / 2;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            Engine.Interrupt.Check();
            var a = Trim(Enumerable.Range(0, n).Select(_ => (long)rng.Next((int)Math.Min(p, int.MaxValue))).ToArray());
            if (a.Length < 2) continue;
            var b = Sub(PowMod(a, e, g, p), [1], p);
            var c = Gcd(g, b, p);
            if (c.Length > 1 && c.Length < g.Length)
            {
                var r = EqualDegree(c, d, p, rng);
                r.AddRange(EqualDegree(DivRem(g, c, p).Q, d, p, rng));
                return r;
            }
        }
        return [g];
    }
}
