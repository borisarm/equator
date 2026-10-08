using System.Numerics;

namespace Equator.Core;

/// <summary>Integer number theory helpers.</summary>
public static class IntMath
{
    public static BigInteger Factorial(int n)
    {
        BigInteger r = 1;
        for (int i = 2; i <= n; i++) r *= i;
        return r;
    }

    public static BigInteger Binomial(BigInteger n, BigInteger k)
    {
        if (k < 0 || k > n) return 0;
        if (k > n - k) k = n - k;
        BigInteger r = 1;
        for (BigInteger i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }

    private static readonly int[] SmallPrimes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];

    public static bool IsPrime(BigInteger n)
    {
        if (n < 2) return false;
        foreach (var p in SmallPrimes)
        {
            if (n == p) return true;
            if (n % p == 0) return false;
        }
        // Miller-Rabin: deterministic for n < 3.3e24 with these bases, probabilistic beyond.
        var d = n - 1; int s = 0;
        while (d.IsEven) { d >>= 1; s++; }
        foreach (var a in SmallPrimes)
        {
            var x = BigInteger.ModPow(a, d, n);
            if (x.IsOne || x == n - 1) continue;
            bool composite = true;
            for (int r = 1; r < s; r++)
            {
                x = BigInteger.ModPow(x, 2, n);
                if (x == n - 1) { composite = false; break; }
            }
            if (composite) return false;
        }
        return true;
    }

    /// <summary>Factors |n| into primes using trial division followed by Pollard's rho.</summary>
    public static SortedDictionary<BigInteger, int> Factor(BigInteger n)
    {
        var result = new SortedDictionary<BigInteger, int>();
        n = BigInteger.Abs(n);
        if (n < 2) return result;
        void Add(BigInteger p) => result[p] = result.TryGetValue(p, out var c) ? c + 1 : 1;
        for (int p = 2; p < 10000 && (BigInteger)p * p <= n; p += p == 2 ? 1 : 2)
        {
            while (n % p == 0) { Add(p); n /= p; }
        }
        var stack = new Stack<BigInteger>();
        if (n > 1) stack.Push(n);
        while (stack.Count > 0)
        {
            var m = stack.Pop();
            if (m == 1) continue;
            if (IsPrime(m)) { Add(m); continue; }
            var d = PollardRho(m);
            stack.Push(d);
            stack.Push(m / d);
        }
        return result;
    }

    private static BigInteger PollardRho(BigInteger n)
    {
        if (n.IsEven) return 2;
        var rng = new Random(12345);
        while (true)
        {
            BigInteger x = rng.Next(2, int.MaxValue) % n, y = x, c = rng.Next(1, int.MaxValue) % n, d = 1;
            while (d.IsOne)
            {
                x = (x * x + c) % n;
                y = (y * y + c) % n;
                y = (y * y + c) % n;
                d = BigInteger.GreatestCommonDivisor(BigInteger.Abs(x - y), n);
            }
            if (d != n) return d;
        }
    }

    /// <summary>Exact integer k-th root if it exists.</summary>
    public static bool TryExactRoot(BigInteger n, int k, out BigInteger root)
    {
        root = 0;
        if (n.Sign < 0) return false;
        if (n < 2) { root = n; return true; }
        var r = IntegerRoot(n, k);
        if (BigInteger.Pow(r, k) == n) { root = r; return true; }
        return false;
    }

    /// <summary>floor(n^(1/k)) via Newton iteration.</summary>
    public static BigInteger IntegerRoot(BigInteger n, int k)
    {
        if (n < 2) return n;
        var bits = (int)(n.GetBitLength() / k) + 1;
        var x = BigInteger.One << bits;
        while (true)
        {
            var y = ((k - 1) * x + n / BigInteger.Pow(x, k - 1)) / k;
            if (y >= x) return x;
            x = y;
        }
    }

    /// <summary>Positive divisors of |n|, capped to keep rational-root searches bounded.</summary>
    public static List<BigInteger> Divisors(BigInteger n)
    {
        var divs = new List<BigInteger> { 1 };
        foreach (var (p, e) in Factor(n))
        {
            int count = divs.Count;
            BigInteger pk = 1;
            for (int i = 1; i <= e; i++)
            {
                pk *= p;
                for (int j = 0; j < count; j++) divs.Add(divs[j] * pk);
            }
            if (divs.Count > 5000) break;
        }
        divs.Sort();
        return divs;
    }
}
