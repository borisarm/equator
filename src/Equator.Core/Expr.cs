using System.Numerics;
using Equator.Core.Printing;

namespace Equator.Core;

/// <summary>
/// Immutable symbolic expression. Instances are always in canonical form: build them through
/// <see cref="Plus(Expr[])"/>, <see cref="Times(Expr[])"/>, <see cref="Power"/> and <see cref="Call(string, Expr[])"/>
/// (or the C# operators), never by instantiating the composite node types directly.
/// </summary>
public abstract class Expr : IEquatable<Expr>, IComparable<Expr>
{
    private int _hash;

    // ---- well-known constants -------------------------------------------------------------
    public static readonly Num Zero = new(BigRational.Zero);
    public static readonly Num One = new(BigRational.One);
    public static readonly Num MinusOne = new(BigRational.MinusOne);
    public static readonly Num Two = new(2);
    public static readonly Num Half = new(BigRational.Half);
    public static readonly Sym Pi = new("Pi");
    public static readonly Sym E = new("E");
    public static readonly Sym I = new("I");
    public static readonly Sym Infinity = new("Infinity");
    public static readonly Sym ComplexInfinity = new("ComplexInfinity");
    public static readonly Sym Undefined = new("Undefined");

    internal abstract int Rank { get; }
    public virtual IReadOnlyList<Expr> Args => [];

    /// <summary>Rebuilds a node of the same kind from new children, re-canonicalising.</summary>
    public virtual Expr WithArgs(IReadOnlyList<Expr> args) => this;

    protected abstract int ComputeHash();
    protected abstract bool StructEquals(Expr other);

    public override int GetHashCode()
    {
        if (_hash == 0) { var h = ComputeHash(); _hash = h == 0 ? 1 : h; }
        return _hash;
    }

    public bool Equals(Expr? other) =>
        other is not null && (ReferenceEquals(this, other) || (GetType() == other.GetType() && GetHashCode() == other.GetHashCode() && StructEquals(other)));

    public override bool Equals(object? obj) => obj is Expr e && Equals(e);
    public static bool operator ==(Expr? a, Expr? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(Expr? a, Expr? b) => !(a == b);

    public override string ToString() => TextFormatter.Format(this);

    // ---- classification helpers ----------------------------------------------------------
    public bool IsNumber => this is Num or Real;
    public bool IsZero => this is Num { Value.IsZero: true } || (this is Real r && (r.Big is { } b ? b.IsZero : r.Value == 0.0));
    public bool IsOne => this is Num { Value.IsOne: true } || (this is Real r && (r.Big is { } b ? b.Mantissa.IsOne && b.Exponent == 0 : r.Value == 1.0));
    public bool IsInteger => this is Num { Value.IsInteger: true };
    public bool IsNegativeNumber => this is Num n ? n.Value.Sign < 0 : this is Real r && r.Value < 0;
    public bool IsInfinite => this == Infinity || this == ComplexInfinity || (this is MulExpr m && m.Factors.Count == 2 && m.Factors[0].IsNumber && m.Factors[1] == Infinity);
    public bool IsUndefinedOrInfinite => this == Undefined || ContainsAny(e => e == Infinity || e == ComplexInfinity || e == Undefined);

    /// <summary>True when the expression has no free symbols other than the numeric constants Pi, E and I.</summary>
    public bool IsConstant => !ContainsAny(e => e is Sym s && !s.IsNumericConstant);

    public bool Contains(Expr sub) => this == sub || Args.Any(a => a.Contains(sub));
    public bool ContainsAny(Func<Expr, bool> pred) => pred(this) || Args.Any(a => a.ContainsAny(pred));
    public bool FreeOf(Expr sub) => !Contains(sub);

    public IEnumerable<Expr> Subexpressions()
    {
        yield return this;
        foreach (var a in Args)
            foreach (var s in a.Subexpressions())
                yield return s;
    }

    public SortedSet<Sym> FreeSymbols()
    {
        var set = new SortedSet<Sym>(Comparer<Sym>.Create((a, b) => string.CompareOrdinal(a.Name, b.Name)));
        foreach (var e in Subexpressions())
            if (e is Sym s && !s.IsSpecial) set.Add(s);
        return set;
    }

    /// <summary>Replaces every occurrence of a subexpression (structural match) and re-canonicalises.</summary>
    public Expr Substitute(Expr from, Expr to) => Substitute(new Dictionary<Expr, Expr> { [from] = to });

    public Expr Substitute(IReadOnlyDictionary<Expr, Expr> map)
    {
        if (map.TryGetValue(this, out var r)) return r;
        if (Args.Count == 0) return this;
        var changed = false;
        var newArgs = new Expr[Args.Count];
        for (int i = 0; i < newArgs.Length; i++)
        {
            newArgs[i] = Args[i].Substitute(map);
            changed |= !ReferenceEquals(newArgs[i], Args[i]);
        }
        return changed ? WithArgs(newArgs) : this;
    }

    /// <summary>Applies <paramref name="f"/> bottom-up to every node.</summary>
    public Expr MapBottomUp(Func<Expr, Expr> f)
    {
        if (Args.Count == 0) return f(this);
        var newArgs = Args.Select(a => a.MapBottomUp(f)).ToArray();
        return f(WithArgs(newArgs));
    }

    public int Complexity()
    {
        int c = this switch
        {
            Num n => n.Value.IsInteger ? 1 : 2,
            _ => 1,
        };
        foreach (var a in Args) c += a.Complexity();
        return c;
    }

    /// <summary>Splits a term into its numeric coefficient and the remaining factor: 3*x*y -> (3, x*y).</summary>
    public (Expr Coeff, Expr Term) SplitCoefficient()
    {
        if (IsNumber) return (this, One);
        if (this is MulExpr m && m.Factors[0].IsNumber)
        {
            var rest = m.Factors.Count == 2 ? m.Factors[1] : new MulExpr(m.Factors.Skip(1).ToArray());
            return (m.Factors[0], rest);
        }
        return (One, this);
    }

    /// <summary>True when the term "looks negative", i.e. has a negative numeric coefficient.</summary>
    public bool HasNegativeCoefficient => SplitCoefficient().Coeff.IsNegativeNumber;

    // ---- ordering ---------------------------------------------------------------------------
    public int CompareTo(Expr? other) => Compare(this, other!);

    public static int Compare(Expr a, Expr b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a.IsNumber && b.IsNumber) return NumericValue(a).CompareTo(NumericValue(b));
        if (a.IsNumber) return -1;
        if (b.IsNumber) return 1;
        if (a is MulExpr || b is MulExpr)
        {
            // Order by monomial first, then by coefficient, so that -y sorts next to (and before) y.
            var (ca, ra) = a.SplitCoefficient();
            var (cb, rb) = b.SplitCoefficient();
            if (!ReferenceEquals(ra, a) || !ReferenceEquals(rb, b))
            {
                int cr = Compare(ra, rb);
                return cr != 0 ? cr : Compare(ca, cb);
            }
        }
        var (ab, ae) = a is PowExpr pa ? (pa.Base, pa.Exponent) : (a, One);
        var (bb, be) = b is PowExpr pb ? (pb.Base, pb.Exponent) : (b, One);
        int c = StructCompare(ab, bb);
        return c != 0 ? c : StructCompare(ae, be);
    }

    private static double NumericValue(Expr e) => e is Num n ? n.Value.ToDouble() : ((Real)e).Value;

    private static int StructCompare(Expr a, Expr b)
    {
        if (a.IsNumber && b.IsNumber)
        {
            int c0 = NumericValue(a).CompareTo(NumericValue(b));
            if (c0 != 0) return c0;
            return (a is Real).CompareTo(b is Real);
        }
        int c = a.Rank.CompareTo(b.Rank);
        if (c != 0) return c;
        switch (a)
        {
            case Sym sa: return string.CompareOrdinal(sa.Name, ((Sym)b).Name);
            case StrExpr s: return string.CompareOrdinal(s.Value, ((StrExpr)b).Value);
            case FnExpr fa:
                {
                    var fb = (FnExpr)b;
                    c = string.CompareOrdinal(fa.Name, fb.Name);
                    if (c != 0) return c;
                    break;
                }
        }
        var xa = a.Args; var xb = b.Args;
        // Compare from the most significant (last) element, as canonical lists are sorted ascending.
        for (int i = 1; i <= Math.Min(xa.Count, xb.Count); i++)
        {
            c = Compare(xa[xa.Count - i], xb[xb.Count - i]);
            if (c != 0) return c;
        }
        return xa.Count.CompareTo(xb.Count);
    }

    // ---- canonical constructors -------------------------------------------------------------
    public static implicit operator Expr(int v) => new Num(v);
    public static implicit operator Expr(BigRational v) => new Num(v);
    public static Expr Number(BigRational v) => new Num(v);
    public static Expr Number(double v) => new Real(v);

    public static Expr operator +(Expr a, Expr b) => Plus(a, b);
    public static Expr operator -(Expr a, Expr b) => Plus(a, Times(MinusOne, b));
    public static Expr operator -(Expr a) => Times(MinusOne, a);
    public static Expr operator *(Expr a, Expr b) => Times(a, b);
    public static Expr operator /(Expr a, Expr b) => Times(a, Power(b, MinusOne));

    public static Expr Sqrt(Expr a) => Power(a, Half);
    public static Expr Exp(Expr a) => Power(E, a);
    public static Expr Call(string name, params Expr[] args) => Functions.Apply(name, args);
    public static Expr Equation(Expr lhs, Expr rhs) => new EqExpr(lhs, rhs);
    public static Expr List(IEnumerable<Expr> items) => new ListExpr(items.ToArray());
    public static Expr List(params Expr[] items) => new ListExpr(items);

    public static Expr Plus(params Expr[] items) => Plus((IEnumerable<Expr>)items);

    public static Expr Plus(IEnumerable<Expr> items)
    {
        BigRational q = BigRational.Zero;
        Approx? r = null;
        var order = new List<Expr>();
        var coeffs = new Dictionary<Expr, (BigRational q, Approx? r)>();
        List<ListExpr>? lists = null;
        int posInf = 0, negInf = 0, cInf = 0;

        void Accumulate(Expr t)
        {
            switch (t)
            {
                case AddExpr a:
                    foreach (var s in a.Terms) Accumulate(s);
                    return;
                case Num n: q += n.Value; return;
                case Real re: r = r is { } r0 ? r0.Add(Approx.Of(re)) : Approx.Of(re); return;
                case ListExpr l: (lists ??= []).Add(l); return;
            }
            if (t == Undefined) { cInf = -1000; return; }
            if (t == ComplexInfinity) { cInf++; return; }
            var (c, rest) = t.SplitCoefficient();
            if (rest == Infinity)
            {
                if (c.IsZero) { cInf = -1000; return; }
                if (c.IsNegativeNumber) negInf++; else posInf++;
                return;
            }
            if (!coeffs.TryGetValue(rest, out var acc)) { order.Add(rest); acc = (BigRational.Zero, null); }
            coeffs[rest] = c is Num cn ? (acc.q + cn.Value, acc.r) : (acc.q, acc.r is { } ar ? ar.Add(Approx.Of((Real)c)) : Approx.Of((Real)c));
        }

        foreach (var item in items) Accumulate(item);

        if (cInf < 0 || (cInf > 0 && posInf + negInf > 0) || cInf > 1 || (posInf > 0 && negInf > 0)) return Undefined;
        if (cInf == 1) return ComplexInfinity;
        if (posInf > 0) return Infinity;
        if (negInf > 0) return Times(MinusOne, Infinity);

        var terms = new List<Expr>();
        foreach (var key in order)
        {
            var (cq, cr) = coeffs[key];
            Expr coeff = cr is { } crv ? crv.AddRational(cq).ToReal() : new Num(cq);
            if (coeff is Num { Value.IsZero: true }) continue;
            if (coeff is Real && coeff.IsZero && cq.IsZero) continue;
            terms.Add(MakeTerm(coeff, key));
        }
        Expr constant = r is { } rv ? rv.AddRational(q).ToReal() : new Num(q);

        if (lists is not null)
        {
            var scalar = Plus(terms.Append(constant));
            return AddLists(lists, scalar);
        }

        if (!constant.IsZero || terms.Count == 0) terms.Add(constant);
        if (terms.Count == 1) return terms[0];
        terms.Sort(Compare);
        return new AddExpr(terms.ToArray());
    }

    private static Expr MakeTerm(Expr coeff, Expr rest)
    {
        if (coeff.IsOne && coeff is Num) return rest;
        if (rest is MulExpr m) return new MulExpr([coeff, .. m.Factors]);
        return new MulExpr([coeff, rest]);
    }

    private static Expr AddLists(List<ListExpr> lists, Expr scalar)
    {
        int n = lists[0].Items.Count;
        if (lists.Any(l => l.Items.Count != n)) throw new MathException(Messages.T("Cannot add lists of different lengths."));
        var items = new Expr[n];
        for (int i = 0; i < n; i++)
            items[i] = Plus(lists.Select(l => l.Items[i]).Append(scalar));
        return new ListExpr(items);
    }

    public static Expr Times(params Expr[] items) => Times((IEnumerable<Expr>)items);

    public static Expr Times(IEnumerable<Expr> items)
    {
        BigRational q = BigRational.One;
        Approx? r = null;
        bool zero = false, undefined = false;
        var order = new List<Expr>();
        var exps = new Dictionary<Expr, List<Expr>>();
        var originals = new Dictionary<Expr, Expr>();
        List<ListExpr>? lists = null;

        void Accumulate(Expr f)
        {
            switch (f)
            {
                case MulExpr m:
                    foreach (var g in m.Factors) Accumulate(g);
                    return;
                case Num n:
                    if (n.Value.IsZero) zero = true; else q *= n.Value;
                    return;
                case Real re:
                    if (re.IsZero) zero = true;
                    r = r is { } r0 ? r0.Mul(Approx.Of(re)) : Approx.Of(re);
                    return;
                case ListExpr l: (lists ??= []).Add(l); return;
            }
            if (f == Undefined) { undefined = true; return; }
            var (b, e) = f is PowExpr p ? (p.Base, p.Exponent) : (f, One);
            if (!exps.TryGetValue(b, out var list)) { exps[b] = list = []; order.Add(b); originals[b] = f; }
            list.Add(e);
        }

        foreach (var item in items) Accumulate(item);
        if (undefined) return Undefined;
        if (zero)
        {
            if (order.Any(b => b == Infinity || b == ComplexInfinity)) return Undefined;
            if (lists is not null) return MulLists(lists, Zero);
            return r.HasValue ? new Real(0.0) : Zero;
        }

        var factors = new List<Expr>();
        foreach (var b in order)
        {
            // A base seen once is already canonical; re-powering it could recurse (e.g. 2^(1/2)).
            var p = exps[b].Count == 1 ? originals[b] : Power(b, Plus(exps[b]));
            AddFactor(p);
        }

        void AddFactor(Expr p)
        {
            switch (p)
            {
                case Num n:
                    if (n.Value.IsZero) zero = true; else q *= n.Value;
                    break;
                case Real re: r = r is { } r1 ? r1.Mul(Approx.Of(re)) : Approx.Of(re); break;
                case MulExpr m:
                    foreach (var g in m.Factors) AddFactor(g);
                    break;
                default:
                    if (p == Undefined) undefined = true; else factors.Add(p);
                    break;
            }
        }

        if (undefined) return Undefined;
        if (zero) return Zero;

        if (factors.Any(f => f == Infinity || f == ComplexInfinity))
        {
            // Numeric coefficients only contribute their sign next to an infinity; so do real constants
            // such as log(2), pi or sqrt(2) (∞·log(2) = ∞).
            for (int i = factors.Count - 1; i >= 0; i--)
            {
                var f = factors[i];
                if (f == Infinity || f == ComplexInfinity || f.IsUndefinedOrInfinite) continue;
                if (!f.IsConstant)
                {
                    // Parameters of known sign (assume(a > 0)): a·∞ = ∞.
                    if (Engine.Assumptions.Active && Engine.Assumptions.Sign(f) is int fs && fs != 0)
                    {
                        if (fs < 0) q = -q;
                        factors.RemoveAt(i);
                    }
                    continue;
                }
                double v;
                try { v = Numerics.NumericEval.EvalReal(f); }
                catch (MathException) { continue; }
                if (double.IsNaN(v) || v == 0 || double.IsInfinity(v)) continue;
                if (v < 0) q = -q;
                factors.RemoveAt(i);
            }
            double sign = q.Sign * (r is { } rs ? rs.Sign : 1);
            q = sign < 0 ? BigRational.MinusOne : BigRational.One;
            r = null;
            if (factors.Contains(ComplexInfinity)) { q = BigRational.One; factors.RemoveAll(f => f == Infinity); }
        }

        Expr coeff = r is { } rc ? rc.MulRational(q).ToReal() : new Num(q);

        if (lists is not null)
        {
            Expr scalar = factors.Count == 0 ? coeff : Times(factors.Prepend(coeff));
            return MulLists(lists, scalar);
        }

        if (factors.Count == 0) return coeff;

        // sqrt(3) sqrt(31) = sqrt(93): positive rational bases with the same fractional exponent combine.
        var radicalGroups = factors
            .Where(f => f is PowExpr { Base: Num { Value.Sign: > 0 }, Exponent: Num { Value.IsInteger: false } })
            .GroupBy(f => ((PowExpr)f).Exponent)
            .Where(g => g.Count() > 1)
            .ToList();
        if (radicalGroups.Count > 0)
        {
            var merged = factors.Where(f => !radicalGroups.Any(g => g.Contains(f))).ToList();
            foreach (var g in radicalGroups)
            {
                var product = g.Aggregate(BigRational.One, (acc, f) => acc * ((Num)((PowExpr)f).Base).Value);
                merged.Add(Power(new Num(product), g.Key));
            }
            merged.Add(coeff);
            return Times(merged);
        }

        bool unitCoeff = coeff.IsOne;
        if (unitCoeff && factors.Count == 1) return factors[0];
        // -(a + b) -> -a - b, as in Mathematica; other coefficients stay outside so factored forms survive.
        if (factors.Count == 1 && factors[0] is AddExpr sum && coeff is Num { Value.IsMinusOne: true })
            return Plus(sum.Terms.Select(t => Times(coeff, t)));
        factors.Sort(Compare);
        if (!unitCoeff) factors.Insert(0, coeff);
        return factors.Count == 1 ? factors[0] : new MulExpr(factors.ToArray());
    }

    private static Expr MulLists(List<ListExpr> lists, Expr scalar)
    {
        int n = lists[0].Items.Count;
        if (lists.Any(l => l.Items.Count != n)) throw new MathException(Messages.T("Cannot multiply lists of different lengths elementwise."));
        var items = new Expr[n];
        for (int i = 0; i < n; i++)
            items[i] = Times(lists.Select(l => l.Items[i]).Append(scalar));
        return new ListExpr(items);
    }

    public static Expr Power(Expr b, Expr e)
    {
        if (b == Undefined || e == Undefined) return Undefined;
        if (b is ListExpr lb) return new ListExpr(lb.Items.Select(x => Power(x, e)).ToArray());
        if (e is ListExpr le) return new ListExpr(le.Items.Select(x => Power(b, x)).ToArray());
        if (e.IsZero)
        {
            if (b.IsInfinite) return Undefined;
            return e is Real ? new Real(1.0) : One;
        }
        if (e is Num { Value.IsOne: true }) return b;
        if (b is Num { Value.IsOne: true }) return e.IsInfinite ? Undefined : One;

        if (b.IsZero)
        {
            if (e.IsNumber) return e.IsNegativeNumber ? ComplexInfinity : b;
            if (e == Infinity) return Zero;
            return new PowExpr(b, e);
        }

        // Infinite bases / exponents.
        if (b == Infinity || b == ComplexInfinity)
        {
            if (e.IsNumber) return e.IsNegativeNumber ? Zero : b;
            // Real constant exponents (sqrt(2)/8, -pi) act like numbers.
            if (e.IsConstant && !e.Contains(I) && !e.IsUndefinedOrInfinite)
            {
                double v;
                try { v = Numerics.NumericEval.EvalReal(e); }
                catch (MathException) { v = double.NaN; }
                if (v < 0) return Zero;
                if (v > 0) return b;
            }
            if (Engine.Assumptions.Active && Engine.Assumptions.Sign(e) is int es && es != 0) return es < 0 ? Zero : b;
            return new PowExpr(b, e);
        }
        if (e == Infinity || (e.IsInfinite && e.HasNegativeCoefficient))
        {
            bool positive = e == Infinity;
            double v = b.IsNumber || b == E || b == Pi ? Numerics.NumericEval.EvalReal(b) : double.NaN;
            if (!double.IsNaN(v))
            {
                if (Math.Abs(v) > 1) return positive ? (v > 0 ? Infinity : ComplexInfinity) : Zero;
                if (Math.Abs(v) < 1) return positive ? Zero : (v > 0 ? Infinity : ComplexInfinity);
                return Undefined;
            }
            return new PowExpr(b, e);
        }

        // Purely numeric cases.
        if (b is Num nb && e is Num ne) return RationalPower(nb.Value, ne.Value);
        if (b.IsNumber && e.IsNumber || (b is Real && e.IsConstant) || (e is Real && b.IsConstant && !b.Contains(I)))
            return NumericPower(b, e);

        if (b == I && e is Num { Value.IsInteger: true } ni)
        {
            var k = (int)(((ni.Value.Num % 4) + 4) % 4);
            return k switch { 0 => One, 1 => I, 2 => MinusOne, _ => Times(MinusOne, I) };
        }

        if (b == I && e is Num { Value: var hi } && hi == BigRational.Half) return (One + I) / Sqrt(Two);
        if (b == E)
        {
            if (e is FnExpr { Name: "log" } lg) return lg.Args[0];
            // e^(c I pi) for rational c with exact trigonometric values
            if (e is MulExpr eip && eip.Factors.Count is 2 or 3 && eip.Factors.Contains(I) && eip.Factors.Contains(Pi))
            {
                var c = eip.Factors.Count == 3 ? eip.Factors[0] : One;
                if (c is Num)
                {
                    var cos = Call("cos", c * Pi);
                    var sin = Call("sin", c * Pi);
                    if (cos is not FnExpr && sin is not FnExpr) return cos + I * sin;
                }
            }
            if (e is MulExpr me)
            {
                var logs = me.Factors.Where(f => f is FnExpr { Name: "log" }).ToList();
                if (logs.Count == 1)
                {
                    var rest = Times(me.Factors.Where(f => !ReferenceEquals(f, logs[0])));
                    if (rest.IsNumber) return Power(logs[0].Args[0], rest);
                }
            }
        }

        if (b is PowExpr pb)
        {
            if (e.IsInteger) return Power(pb.Base, Times(pb.Exponent, e));
            if (pb.Exponent is Num pe && pe.Value > BigRational.MinusOne && pe.Value <= BigRational.One)
                return Power(pb.Base, Times(pb.Exponent, e));
            if (Engine.Assumptions.Active)
            {
                // (x^a)^b = x^(a b) for x ≥ 0 and real a, b.
                if (Engine.Assumptions.IsNonNegative(pb.Base) && Engine.Assumptions.IsReal(pb.Exponent) && Engine.Assumptions.IsReal(e))
                    return Power(pb.Base, Times(pb.Exponent, e));
                // (x^(2k))^(p/(2k)) = |x|^p for real x.
                if (pb.Exponent is Num { Value: var inner } && inner.IsInteger && inner.Num.IsEven && e is Num { Value: var outer }
                    && (inner * outer).IsInteger && Engine.Assumptions.IsReal(pb.Base))
                    return Power(Call("abs", pb.Base), new Num(inner * outer));
            }
        }

        if (Engine.Assumptions.Active)
        {
            // (−1)^k for an integer k: decided by parity.
            if (b is Num { Value.IsMinusOne: true } && !e.IsNumber && Engine.Assumptions.IsInteger(e))
            {
                if (Engine.Assumptions.IsInteger(e / 2)) return One;
                if (Engine.Assumptions.IsInteger((e - 1) / 2)) return MinusOne;
            }
            // Fractional powers of products: pull out the factors known to be non-negative.
            if (b is MulExpr mp && !e.IsInteger && Engine.Assumptions.IsReal(e))
            {
                var nonneg = mp.Factors.Where(f => !f.IsNumber && Engine.Assumptions.IsNonNegative(f)).ToList();
                if (nonneg.Count > 0 && nonneg.Count < mp.Factors.Count)
                {
                    var rest = Times(mp.Factors.Where(f => !nonneg.Contains(f)));
                    return Times(nonneg.Select(f => Power(f, e)).Append(Power(rest, e)));
                }
                if (nonneg.Count == mp.Factors.Count)
                    return Times(mp.Factors.Select(f => Power(f, e)));
            }
        }

        if (b is MulExpr mb)
        {
            if (e.IsInteger) return Times(mb.Factors.Select(f => Power(f, e)));
            if (mb.Factors.Count == 2 && mb.Factors[0] is Num { Value.IsMinusOne: true } && mb.Factors[1] == I && e is Num { Value: var h2 } && h2 == BigRational.Half)
                return (One - I) / Sqrt(Two);
            // (c i)^e = c^e i^e for real c > 0 (both sides have argument π/2), and (−c i)^e = c^e (−i)^e.
            if (e is Num && mb.Factors.Contains(I))
            {
                var k = Times(mb.Factors.Where(f => f != I));
                if (!k.IsOne && !(k is Num { Value.IsMinusOne: true }) && k.IsConstant && !k.Contains(I))
                {
                    double kv;
                    try { kv = Numerics.NumericEval.EvalReal(k); }
                    catch (MathException) { kv = double.NaN; }
                    if (kv > 0) return Times(Power(k, e), Power(I, e));
                    if (kv < 0) return Times(Power(-k, e), Power(Times(MinusOne, I), e));
                }
            }
            // Pull positive numeric coefficients out: (4x)^(1/2) = 2 x^(1/2).
            if (mb.Factors[0] is Num c && c.Value.Sign > 0)
            {
                var rest = mb.Factors.Count == 2 ? mb.Factors[1] : new MulExpr(mb.Factors.Skip(1).ToArray());
                return Times(Power(c, e), Power(rest, e));
            }
        }

        return new PowExpr(b, e);
    }

    private static Expr NumericPower(Expr b, Expr e)
    {
        // Arbitrary-precision operands (and no machine reals): evaluate at their precision.
        bool anyMachine = b.ContainsAny(z => z is Real { IsMachine: true }) || e.ContainsAny(z => z is Real { IsMachine: true });
        var precB = Numerics.BigEval.PrecisionOf(b);
        var precE = Numerics.BigEval.PrecisionOf(e);
        if (!anyMachine && (precB is not null || precE is not null))
        {
            int digits = Math.Min(precB ?? int.MaxValue, precE ?? int.MaxValue);
            int bits = Numerics.BigFloat.DigitsToBits(digits) + 16;
            var bb = Numerics.BigEval.Eval(b, bits);
            if (e is Num { Value.IsInteger: true } ni && System.Numerics.BigInteger.Abs(ni.Value.Num) < 1_000_000)
                return Numerics.BigEval.ToExpr(Numerics.BigEval.IntPow(bb, (int)ni.Value.Num, bits), digits);
            return Numerics.BigEval.ToExpr(Numerics.BigEval.Pow(bb, Numerics.BigEval.Eval(e, bits), bits), digits);
        }
        var cb = Numerics.NumericEval.Eval(b);
        var ce = Numerics.NumericEval.Eval(e);
        if (cb.Imaginary == 0 && ce.Imaginary == 0 && (cb.Real >= 0 || Math.Abs(ce.Real - Math.Round(ce.Real)) < 1e-15))
            return new Real(Math.Pow(cb.Real, ce.Real));
        return Numerics.NumericEval.FromComplex(Complex.Pow(cb, ce));
    }

    private static Expr RationalPower(BigRational b, BigRational e)
    {
        if (e.IsInteger)
        {
            // Guard against absurdly large results.
            if (BigInteger.Abs(e.Num) > 100000 ||
                (double)BigInteger.Abs(e.Num) * Math.Max(b.Num.GetBitLength(), b.Den.GetBitLength()) > 4_000_000)
                return new PowExpr(new Num(b), new Num(e));
            return new Num(BigRational.Pow(b, (int)e.Num));
        }

        if (b.IsMinusOne)
        {
            if (e.Den == 2) return Power(I, new Num(e.Num));
            return new PowExpr(MinusOne, new Num(e));
        }
        if (b.Sign < 0)
        {
            if (e.Den == 2)
            {
                // (-a)^(p/2) = I^p a^(p/2) on the principal branch.
                return Times(Power(I, new Num(e.Num)), RationalPower(-b, e));
            }
            return Times(new PowExpr(MinusOne, new Num(e)), RationalPower(-b, e));
        }

        // Split off the integer part of the exponent: 2^(3/2) = 2 * 2^(1/2).
        var whole = e.Floor();
        var frac = e - whole;
        var result = new List<Expr>();
        if (!whole.IsZero) result.Add(RationalPower(b, whole));
        var p = frac.Num; var qd = (int)frac.Den;
        foreach (var (part, sign) in new[] { (b.Num, 1), (b.Den, -1) })
        {
            if (part.IsOne) continue;
            var (outside, inside) = ExtractPerfectPowers(part, qd);
            if (!outside.IsOne) result.Add(new Num(BigRational.Pow(new BigRational(outside), (int)p * sign)));
            if (!inside.IsOne) result.Add(new PowExpr(new Num(inside), new Num(new BigRational(p * sign, qd))));
        }
        return result.Count == 0 ? One : Times(result);
    }

    /// <summary>Writes n = outside^q * inside with inside free of q-th powers (best effort for big n).</summary>
    private static (BigInteger Outside, BigInteger Inside) ExtractPerfectPowers(BigInteger n, int q)
    {
        if (IntMath.TryExactRoot(n, q, out var root)) return (root, 1);
        if (n.GetBitLength() > 160) return (1, n);
        BigInteger outside = 1, inside = 1;
        foreach (var (prime, mult) in IntMath.Factor(n))
        {
            outside *= BigInteger.Pow(prime, mult / q);
            inside *= BigInteger.Pow(prime, mult % q);
        }
        return (outside, inside);
    }
}

public class MathException(string message) : Exception(message);

// ---- node types -------------------------------------------------------------------------------

public sealed class Num(BigRational value) : Expr
{
    public BigRational Value { get; } = value;
    internal override int Rank => 0;
    protected override int ComputeHash() => Value.GetHashCode();
    protected override bool StructEquals(Expr other) => ((Num)other).Value == Value;
}

/// <summary>
/// Approximate real number: a machine double, or an arbitrary-precision <see cref="Numerics.BigFloat"/> that
/// carries its precision in decimal digits. Arithmetic mixing the two falls back to machine precision;
/// arbitrary-precision operands combine at the smaller precision.
/// </summary>
public sealed class Real : Expr
{
    public Real(double value) { Value = value; }

    public Real(Numerics.BigFloat big, int digits)
    {
        Big = big;
        Digits = Math.Max(digits, 1);
        Value = big.ToDouble();
    }

    /// <summary>Machine approximation (always available).</summary>
    public double Value { get; }
    /// <summary>The arbitrary-precision value, or null for machine numbers.</summary>
    public Numerics.BigFloat? Big { get; }
    /// <summary>Significant decimal digits of an arbitrary-precision number (0 for machine numbers).</summary>
    public int Digits { get; }
    public bool IsMachine => Big is null;

    internal override int Rank => 0;
    protected override int ComputeHash() => Big is { } b ? HashCode.Combine(7, b, Digits) : HashCode.Combine(7, Value);

    protected override bool StructEquals(Expr other)
    {
        var o = (Real)other;
        if (Big is { } a) return o.Big is { } b && a.Equals(b) && Digits == o.Digits;
        return o.Big is null && o.Value.Equals(Value);
    }
}

/// <summary>Accumulates approximate numbers for the canonical constructors, tracking precision.</summary>
internal readonly struct Approx
{
    public readonly double D;
    public readonly Numerics.BigFloat? B;
    public readonly int Digits;

    private Approx(double d, Numerics.BigFloat? b, int digits) { D = d; B = b; Digits = digits; }

    public static Approx Of(Real r) => r.Big is { } b ? new(r.Value, b, r.Digits) : new(r.Value, null, 0);
    public static Approx Machine(double d) => new(d, null, 0);

    private static int Bits(int digits) => Numerics.BigFloat.DigitsToBits(digits) + 16;

    public Approx Add(Approx o)
    {
        if (B is { } a && o.B is { } b)
        {
            int digits = Math.Min(Digits, o.Digits);
            return new Approx(0, Numerics.BigFloat.Add(a, b, Bits(digits)), digits).WithDouble();
        }
        return Machine(D + o.D);
    }

    public Approx Mul(Approx o)
    {
        if (B is { } a && o.B is { } b)
        {
            int digits = Math.Min(Digits, o.Digits);
            return new Approx(0, (a * b).WithPrecision(Bits(digits)), digits).WithDouble();
        }
        return Machine(D * o.D);
    }

    public Approx AddRational(BigRational q) =>
        B is { } a ? new Approx(0, Numerics.BigFloat.Add(a, Numerics.BigFloat.FromRational(q, Bits(Digits)), Bits(Digits)), Digits).WithDouble() : Machine(D + q.ToDouble());

    public Approx MulRational(BigRational q) =>
        B is { } a ? new Approx(0, (a * Numerics.BigFloat.FromRational(q, Bits(Digits))).WithPrecision(Bits(Digits)), Digits).WithDouble() : Machine(D * q.ToDouble());

    private Approx WithDouble() => new(B!.Value.ToDouble(), B, Digits);

    public bool IsZero => B is { } b ? b.IsZero : D == 0;
    public int Sign => B is { } b ? b.Sign : Math.Sign(D);
    public Real ToReal() => B is { } b ? new Real(b, Digits) : new Real(D);
}

public sealed class Sym(string name) : Expr
{
    public string Name { get; } = name;
    internal override int Rank => 1;
    public bool IsNumericConstant => Name is "Pi" or "E" or "I";
    public bool IsSpecial => IsNumericConstant || Name is "Infinity" or "ComplexInfinity" or "Undefined";
    protected override int ComputeHash() => HashCode.Combine(11, Name);
    protected override bool StructEquals(Expr other) => ((Sym)other).Name == Name;
}

public sealed class StrExpr(string value) : Expr
{
    public string Value { get; } = value;
    internal override int Rank => 8;
    protected override int ComputeHash() => HashCode.Combine(13, Value);
    protected override bool StructEquals(Expr other) => ((StrExpr)other).Value == Value;
}

public abstract class CompositeExpr(Expr[] args) : Expr
{
    private readonly Expr[] _args = args;
    public override IReadOnlyList<Expr> Args => _args;

    protected override int ComputeHash()
    {
        var h = new HashCode();
        h.Add(GetType().Name);
        if (this is FnExpr f) h.Add(f.Name);
        foreach (var a in _args) h.Add(a);
        return h.ToHashCode();
    }

    protected override bool StructEquals(Expr other)
    {
        var o = (CompositeExpr)other;
        if (o._args.Length != _args.Length) return false;
        if (this is FnExpr f && ((FnExpr)other).Name != f.Name) return false;
        for (int i = 0; i < _args.Length; i++)
            if (!_args[i].Equals(o._args[i])) return false;
        return true;
    }
}

public sealed class AddExpr : CompositeExpr
{
    internal AddExpr(Expr[] terms) : base(terms) { }
    public IReadOnlyList<Expr> Terms => Args;
    internal override int Rank => 5;
    public override Expr WithArgs(IReadOnlyList<Expr> args) => Plus(args);
}

public sealed class MulExpr : CompositeExpr
{
    internal MulExpr(Expr[] factors) : base(factors) { }
    public IReadOnlyList<Expr> Factors => Args;
    internal override int Rank => 2;
    public override Expr WithArgs(IReadOnlyList<Expr> args) => Times(args);
}

public sealed class PowExpr : CompositeExpr
{
    internal PowExpr(Expr b, Expr e) : base([b, e]) { }
    public Expr Base => Args[0];
    public Expr Exponent => Args[1];
    internal override int Rank => 3;
    public override Expr WithArgs(IReadOnlyList<Expr> args) => Power(args[0], args[1]);
}

public sealed class FnExpr : CompositeExpr
{
    internal FnExpr(string name, Expr[] args) : base(args) { Name = name; }
    public string Name { get; }
    internal override int Rank => 4;
    public override Expr WithArgs(IReadOnlyList<Expr> args) => Call(Name, args.ToArray());

    /// <summary>Creates an unevaluated function node without applying any rules.</summary>
    public static FnExpr Inert(string name, params Expr[] args) => new(name, args);
}

public sealed class ListExpr : CompositeExpr
{
    internal ListExpr(Expr[] items) : base(items) { }
    public IReadOnlyList<Expr> Items => Args;
    internal override int Rank => 6;
    public override Expr WithArgs(IReadOnlyList<Expr> args) => new ListExpr(args.ToArray());

    /// <summary>Rows of equal length whose entries are not lists themselves (a list of matrices is not a matrix).</summary>
    public bool IsMatrix => Items.Count > 0 && Items.All(i => i is ListExpr l && l.Items.Count == ((ListExpr)Items[0]).Items.Count && l.Items.Count > 0 && l.Items.All(x => x is not ListExpr));
}

public sealed class EqExpr : CompositeExpr
{
    internal EqExpr(Expr lhs, Expr rhs) : base([lhs, rhs]) { }
    public Expr Lhs => Args[0];
    public Expr Rhs => Args[1];
    internal override int Rank => 7;
    public override Expr WithArgs(IReadOnlyList<Expr> args) => new EqExpr(args[0], args[1]);
}
