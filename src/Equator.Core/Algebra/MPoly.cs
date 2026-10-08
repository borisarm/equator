using System.Numerics;

namespace Equator.Core.Algebra;

/// <summary>Exponent vector of a monomial; compared by value.</summary>
public readonly struct Mono : IEquatable<Mono>
{
    private readonly int[] _e;
    private readonly int _hash;

    public Mono(int[] e)
    {
        _e = e;
        var h = new HashCode();
        foreach (var x in e) h.Add(x);
        _hash = h.ToHashCode();
    }

    public int this[int i] => _e[i];
    public int Length => _e.Length;
    public int Total => _e.Sum();
    public int[] ToArray() => (int[])_e.Clone();

    public static Mono operator *(Mono a, Mono b)
    {
        var r = new int[a._e.Length];
        for (int i = 0; i < r.Length; i++) r[i] = a._e[i] + b._e[i];
        return new Mono(r);
    }

    /// <summary>a / b when every exponent of b is at most that of a.</summary>
    public bool TryDivide(Mono b, out Mono q)
    {
        var r = new int[_e.Length];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = _e[i] - b._e[i];
            if (r[i] < 0) { q = default; return false; }
        }
        q = new Mono(r);
        return true;
    }

    public Mono With(int var, int exponent)
    {
        var r = (int[])_e.Clone();
        r[var] = exponent;
        return new Mono(r);
    }

    /// <summary>Lexicographic order with variable 0 most significant.</summary>
    public static int CompareLex(Mono a, Mono b)
    {
        for (int i = 0; i < a._e.Length; i++)
            if (a._e[i] != b._e[i]) return a._e[i].CompareTo(b._e[i]);
        return 0;
    }

    public bool Equals(Mono other)
    {
        if (_hash != other._hash || _e.Length != other._e.Length) return false;
        for (int i = 0; i < _e.Length; i++) if (_e[i] != other._e[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Mono m && Equals(m);
    public override int GetHashCode() => _hash;
}

/// <summary>
/// Sparse multivariate polynomial with exact rational coefficients in a fixed number of variables
/// (indices 0..NVars-1). Immutable.
/// </summary>
public sealed class MPoly
{
    private readonly Dictionary<Mono, BigRational> _t;
    public int NVars { get; }

    private MPoly(int n, Dictionary<Mono, BigRational> terms)
    {
        NVars = n;
        _t = terms;
    }

    public static MPoly Zero(int n) => new(n, []);
    public static MPoly Constant(int n, BigRational c) => c.IsZero ? Zero(n) : new(n, new() { [new Mono(new int[n])] = c });

    public static MPoly Var(int n, int i, int power = 1)
    {
        var e = new int[n];
        e[i] = power;
        return new(n, new() { [new Mono(e)] = BigRational.One });
    }

    public static MPoly FromTerms(int n, IEnumerable<(Mono M, BigRational C)> terms)
    {
        var d = new Dictionary<Mono, BigRational>();
        foreach (var (m, c) in terms)
        {
            if (c.IsZero) continue;
            if (d.TryGetValue(m, out var old))
            {
                var s = old + c;
                if (s.IsZero) d.Remove(m); else d[m] = s;
            }
            else d[m] = c;
        }
        return new(n, d);
    }

    public IEnumerable<(Mono M, BigRational C)> Terms => _t.Select(kv => (kv.Key, kv.Value));
    public int TermCount => _t.Count;
    public bool IsZero => _t.Count == 0;
    public bool IsConstant => _t.Count == 0 || (_t.Count == 1 && _t.Keys.First().Total == 0);
    public BigRational ConstantValue => _t.TryGetValue(new Mono(new int[NVars]), out var c) ? c : BigRational.Zero;
    public bool IsOne => IsConstant && ConstantValue.IsOne;

    public int Degree(int var) => _t.Count == 0 ? -1 : _t.Keys.Max(m => m[var]);
    public int TotalDegree => _t.Count == 0 ? -1 : _t.Keys.Max(m => m.Total);
    public bool Contains(int var) => _t.Keys.Any(m => m[var] > 0);
    public IEnumerable<int> Variables => Enumerable.Range(0, NVars).Where(Contains);

    /// <summary>Leading monomial and coefficient in lexicographic order.</summary>
    public (Mono M, BigRational C) Lead
    {
        get
        {
            var best = _t.First();
            foreach (var kv in _t) if (Mono.CompareLex(kv.Key, best.Key) > 0) best = kv;
            return (best.Key, best.Value);
        }
    }

    // ---- arithmetic --------------------------------------------------------------------------------

    public static MPoly operator +(MPoly a, MPoly b)
    {
        var d = new Dictionary<Mono, BigRational>(a._t);
        foreach (var (m, c) in b._t)
        {
            if (d.TryGetValue(m, out var old))
            {
                var s = old + c;
                if (s.IsZero) d.Remove(m); else d[m] = s;
            }
            else d[m] = c;
        }
        return new(a.NVars, d);
    }

    public static MPoly operator -(MPoly a) => new(a.NVars, a._t.ToDictionary(kv => kv.Key, kv => -kv.Value));
    public static MPoly operator -(MPoly a, MPoly b) => a + (-b);

    public static MPoly operator *(MPoly a, MPoly b)
    {
        if (a.IsZero || b.IsZero) return Zero(a.NVars);
        var d = new Dictionary<Mono, BigRational>();
        foreach (var (ma, ca) in a._t)
            foreach (var (mb, cb) in b._t)
            {
                var m = ma * mb;
                var c = ca * cb;
                if (d.TryGetValue(m, out var old))
                {
                    var s = old + c;
                    if (s.IsZero) d.Remove(m); else d[m] = s;
                }
                else d[m] = c;
            }
        return new(a.NVars, d);
    }

    public static MPoly operator *(BigRational k, MPoly a) =>
        k.IsZero ? Zero(a.NVars) : new(a.NVars, a._t.ToDictionary(kv => kv.Key, kv => kv.Value * k));

    public MPoly MulMono(Mono m, BigRational c) =>
        c.IsZero ? Zero(NVars) : new(NVars, _t.ToDictionary(kv => kv.Key * m, kv => kv.Value * c));

    public MPoly Pow(int n)
    {
        var r = Constant(NVars, BigRational.One);
        var b = this;
        while (n > 0)
        {
            if ((n & 1) == 1) r *= b;
            n >>= 1;
            if (n > 0) b *= b;
        }
        return r;
    }

    public override bool Equals(object? obj) =>
        obj is MPoly p && p.NVars == NVars && p._t.Count == _t.Count && _t.All(kv => p._t.TryGetValue(kv.Key, out var c) && c == kv.Value);

    public override int GetHashCode() => _t.Aggregate(NVars, (h, kv) => h ^ HashCode.Combine(kv.Key, kv.Value));

    // ---- views in one variable ----------------------------------------------------------------------

    /// <summary>Coefficients c_k (polynomials free of var) with this = Σ c_k var^k.</summary>
    public List<MPoly> CoefficientsIn(int var)
    {
        int deg = Math.Max(Degree(var), 0);
        var buckets = new List<Dictionary<Mono, BigRational>>();
        for (int k = 0; k <= deg; k++) buckets.Add([]);
        foreach (var (m, c) in _t) buckets[m[var]][m.With(var, 0)] = c;
        return buckets.Select(b => new MPoly(NVars, b)).ToList();
    }

    public MPoly CoefficientIn(int var, int k)
    {
        var d = new Dictionary<Mono, BigRational>();
        foreach (var (m, c) in _t) if (m[var] == k) d[m.With(var, 0)] = c;
        return new(NVars, d);
    }

    public MPoly LeadingCoefficientIn(int var) => CoefficientIn(var, Degree(var));

    public static MPoly FromCoefficients(int var, IReadOnlyList<MPoly> coeffs)
    {
        int n = coeffs[0].NVars;
        var d = new Dictionary<Mono, BigRational>();
        for (int k = 0; k < coeffs.Count; k++)
            foreach (var (m, c) in coeffs[k]._t) d[m.With(var, m[var] + k)] = c;
        return new(n, d);
    }

    public MPoly Derivative(int var)
    {
        var d = new Dictionary<Mono, BigRational>();
        foreach (var (m, c) in _t)
            if (m[var] > 0) d[m.With(var, m[var] - 1)] = c * m[var];
        return new(NVars, d);
    }

    /// <summary>Substitutes a number for one variable.</summary>
    public MPoly Evaluate(int var, BigRational value)
    {
        var powers = new Dictionary<int, BigRational>();
        BigRational Pw(int k)
        {
            if (!powers.TryGetValue(k, out var p)) powers[k] = p = BigRational.Pow(value, k);
            return p;
        }
        return FromTerms(NVars, _t.Select(kv => (kv.Key.With(var, 0), kv.Value * Pw(kv.Key[var]))));
    }

    /// <summary>Substitutes var → var + shift.</summary>
    public MPoly Shift(int var, BigRational shift)
    {
        if (shift.IsZero) return this;
        var terms = new List<(Mono, BigRational)>();
        foreach (var (m, c) in _t)
        {
            int e = m[var];
            // (v + s)^e = Σ binom(e, j) v^j s^(e-j)
            BigInteger binom = 1;
            for (int j = 0; j <= e; j++)
            {
                if (j > 0) binom = binom * (e - j + 1) / j;
                terms.Add((m.With(var, j), c * binom * BigRational.Pow(shift, e - j)));
            }
        }
        return FromTerms(NVars, terms);
    }

    /// <summary>Applies f to every coefficient.</summary>
    public MPoly MapCoefficients(Func<BigRational, BigRational> f) => FromTerms(NVars, _t.Select(kv => (kv.Key, f(kv.Value))));

    /// <summary>Terms whose total degree in the given variables equals k.</summary>
    public MPoly HomogeneousPart(IReadOnlyList<int> vars, int k) =>
        new(NVars, _t.Where(kv => vars.Sum(v => kv.Key[v]) == k).ToDictionary(kv => kv.Key, kv => kv.Value));

    // ---- division and content -----------------------------------------------------------------------

    /// <summary>Exact division over Q; null when b does not divide a.</summary>
    public static MPoly? TryDivide(MPoly a, MPoly b)
    {
        if (b.IsZero) throw new DivideByZeroException();
        if (a.IsZero) return a;
        if (b.IsConstant) return (BigRational.One / b.ConstantValue) * a;
        int v = b.Variables.First();
        int db = b.Degree(v);
        var lcB = b.CoefficientIn(v, db);
        var q = Zero(a.NVars);
        var rem = a;
        int guard = 0;
        while (!rem.IsZero)
        {
            Engine.Interrupt.Check();
            int d = rem.Degree(v);
            if (d < db) return null;
            var t = TryDivide(rem.CoefficientIn(v, d), lcB);
            if (t is null) return null;
            var term = t * Var(a.NVars, v, d - db);
            q += term;
            rem -= term * b;
            if (++guard > 100000) return null;
        }
        return q;
    }

    /// <summary>Positive rational c such that this / c has coprime integer coefficients (sign from the lex-leading coefficient).</summary>
    public BigRational RationalContent()
    {
        if (IsZero) return BigRational.One;
        BigInteger g = 0, l = 1;
        foreach (var c in _t.Values)
        {
            g = BigInteger.GreatestCommonDivisor(g, c.Num);
            l = l / BigInteger.GreatestCommonDivisor(l, c.Den) * c.Den;
        }
        var content = new BigRational(g, l);
        return Lead.C.Sign < 0 ? -content : content;
    }

    /// <summary>Integer-coefficient primitive form with positive lex-leading coefficient.</summary>
    public MPoly Normalized() => IsZero ? this : (BigRational.One / RationalContent()) * this;

    public bool HasIntegerCoefficients => _t.Values.All(c => c.IsInteger);

    // ---- univariate bridges ---------------------------------------------------------------------------

    /// <summary>Coefficients in var (index = power) when this involves no other variable.</summary>
    public BigRational[] UnivariateCoefficients(int var)
    {
        var r = new BigRational[Math.Max(Degree(var), 0) + 1];
        for (int i = 0; i < r.Length; i++) r[i] = BigRational.Zero;
        foreach (var (m, c) in _t) r[m[var]] = c;
        return r;
    }

    public static MPoly FromUnivariate(int n, int var, IReadOnlyList<BigRational> coeffs) =>
        FromTerms(n, coeffs.Select((c, i) => (new Mono(new int[n]).With(var, i), c)));

    public QPoly ToQPoly(int var) => new(UnivariateCoefficients(var));

    // ---- conversion to and from expressions ----------------------------------------------------------

    public Expr ToExpr(IReadOnlyList<Expr> gens)
    {
        var terms = new List<Expr>();
        foreach (var (m, c) in _t)
        {
            var factors = new List<Expr> { new Num(c) };
            for (int i = 0; i < NVars; i++)
                if (m[i] > 0) factors.Add(Expr.Power(gens[i], m[i]));
            terms.Add(Expr.Times(factors));
        }
        return Expr.Plus(terms);
    }
}
