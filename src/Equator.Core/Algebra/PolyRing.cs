using System.Numerics;

namespace Equator.Core.Algebra;

/// <summary>
/// Chooses polynomial generators ("kernels") for a set of expressions and converts between expressions and
/// <see cref="MPoly"/>. Powers of a common base share one generator: with x and sqrt(x) present the generator
/// is x^(1/2) and x becomes its square; b^(n u) for integer n becomes (b^u)^n. Both rewritings are exact
/// identities, so factorisations in the generators are valid for the original expressions.
/// </summary>
public sealed class PolyRing
{
    public IReadOnlyList<Expr> Gens => _gens;
    public int NVars => _gens.Count;

    private readonly List<Expr> _gens = [];
    // Plain bases (x, sin(x), 2, sqrt-able sums): base -> (generator index, q) with generator = base^(1/q).
    private readonly Dictionary<Expr, (int Index, int Q)> _bases = new();
    // Exponential families b^(c u): (b, u) -> generator index of b^u.
    private readonly Dictionary<(Expr B, Expr U), int> _exps = new();

    private PolyRing() { }

    private enum Kind { Plain, Exponential }

    /// <summary>Decomposes a non-numeric factor into a generator family and an exponent; null if not polynomial.</summary>
    private static (Kind Kind, Expr Key1, Expr Key2, BigRational Exp)? Classify(Expr f)
    {
        if (f is PowExpr p)
        {
            if (p.Exponent is Num { Value: var r })
            {
                if (r.Sign <= 0) return null;
                if (r.IsInteger && p.Base is AddExpr) return null; // not expanded
                return (Kind.Plain, p.Base, p.Base, r);
            }
            var (c, u) = p.Exponent.SplitCoefficient();
            if (c is Num { Value: var cn } && cn.IsInteger && cn.Sign > 0 && !p.Base.ContainsAny(z => z is Real))
                return (Kind.Exponential, p.Base, u, cn);
            if (p.Exponent.HasNegativeCoefficient) return null;
            return (Kind.Plain, f, f, BigRational.One);
        }
        if (f is AddExpr) return null;
        return (Kind.Plain, f, f, BigRational.One);
    }

    private static IEnumerable<Expr> FactorsOf(Expr term) => term is MulExpr m ? m.Factors : [term];
    private static IEnumerable<Expr> TermsOf(Expr e) => e is AddExpr a ? a.Terms : [e];

    /// <summary>Builds a ring for expanded polynomial expressions; null if one of them is not polynomial in any generators.</summary>
    public static PolyRing? For(IEnumerable<Expr> exprs)
    {
        var ring = new PolyRing();
        var plainDen = new Dictionary<Expr, BigInteger>();
        var plainOrder = new List<Expr>();
        var expOrder = new List<(Expr, Expr)>();
        foreach (var e in exprs)
            foreach (var t in TermsOf(e))
                foreach (var f in FactorsOf(t))
                {
                    if (f is Num) continue;
                    if (f is Real) return null;
                    var cls = Classify(f);
                    if (cls is null) return null;
                    var (kind, k1, k2, r) = cls.Value;
                    if (kind == Kind.Plain)
                    {
                        if (!plainDen.TryGetValue(k1, out var d)) { plainOrder.Add(k1); d = 1; }
                        plainDen[k1] = d / BigInteger.GreatestCommonDivisor(d, r.Den) * r.Den;
                    }
                    else if (!expOrder.Contains((k1, k2))) expOrder.Add((k1, k2));
                }
        var gens = new List<(Expr Gen, Action<int> Register)>();
        foreach (var b in plainOrder)
        {
            var q = (int)plainDen[b];
            var gen = q == 1 ? b : Expr.Power(b, new Num(new BigRational(1, q)));
            gens.Add((gen, i => ring._bases[b] = (i, q)));
        }
        foreach (var (b, u) in expOrder)
            gens.Add((Expr.Power(b, u), i => ring._exps[(b, u)] = i));
        gens.Sort((a, b) => Expr.Compare(a.Gen, b.Gen));
        for (int i = 0; i < gens.Count; i++)
        {
            ring._gens.Add(gens[i].Gen);
            gens[i].Register(i);
        }
        return ring;
    }

    /// <summary>Converts an expanded polynomial expression; null when it is not polynomial in this ring's generators.</summary>
    public MPoly? ToPoly(Expr e)
    {
        var terms = new List<(Mono, BigRational)>();
        foreach (var t in TermsOf(e))
        {
            var exps = new int[NVars];
            BigRational coeff = BigRational.One;
            foreach (var f in FactorsOf(t))
            {
                if (f is Num n) { coeff *= n.Value; continue; }
                var cls = Classify(f);
                if (cls is null) return null;
                var (kind, k1, k2, r) = cls.Value;
                if (kind == Kind.Plain)
                {
                    if (!_bases.TryGetValue(k1, out var g)) return null;
                    var k = r * g.Q;
                    if (!k.IsInteger) return null;
                    exps[g.Index] += (int)k.Num;
                }
                else
                {
                    if (!_exps.TryGetValue((k1, k2), out var gi)) return null;
                    exps[gi] += (int)r.Num;
                }
            }
            terms.Add((new Mono(exps), coeff));
        }
        return MPoly.FromTerms(NVars, terms);
    }

    public Expr ToExpr(MPoly p) => p.ToExpr(_gens);
}
