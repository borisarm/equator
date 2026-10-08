using System.Globalization;

namespace Equator.Core.Printing;

/// <summary>
/// Presentation tree shared by the text, LaTeX and typeset renderers. It captures how a mathematician would
/// write the expression (fractions, negation, radicals, ordered terms) independently of the output medium.
/// </summary>
public abstract record D
{
    /// <summary>Binding strength, used by renderers to decide where parentheses are needed.</summary>
    public abstract int Prec { get; }
}

public enum AtomKind { Number, Variable, Constant, Text }

public sealed record DAtom(string Display, string Plain, string Latex, AtomKind Kind) : D
{
    public override int Prec => Plain.StartsWith('-') ? 2 : 10;
}

public sealed record DSum(IReadOnlyList<(bool Negative, D Term)> Terms) : D { public override int Prec => 1; }
public sealed record DNeg(D Inner) : D { public override int Prec => 2; }
public sealed record DProd(IReadOnlyList<D> Factors) : D { public override int Prec => 3; }
public sealed record DFrac(D Num, D Den) : D { public override int Prec => 3; }
public sealed record DPow(D Base, D Exp) : D { public override int Prec => 5; }
public sealed record DSci(string Mantissa, int Exponent) : D { public override int Prec => 3; }
public sealed record DSqrt(D Arg, D? Index) : D { public override int Prec => 10; }
public sealed record DCall(string Name, string Plain, IReadOnlyList<D> Args) : D { public override int Prec => 10; }
public sealed record DAbs(D Inner) : D { public override int Prec => 10; }
public sealed record DFactorial(D Inner) : D { public override int Prec => 6; }
public sealed record DList(IReadOnlyList<D> Items) : D { public override int Prec => 10; }
public sealed record DMatrix(IReadOnlyList<IReadOnlyList<D>> Rows) : D { public override int Prec => 10; }
public sealed record DEq(D Lhs, D Rhs, string Op = "=") : D { public override int Prec => 0; }
public sealed record DIntegral(D Body, D Var, D? Lower, D? Upper) : D { public override int Prec => 2; }
public sealed record DBigSum(string Op, D Body, D Var, D Lower, D Upper) : D { public override int Prec => 2; }
public sealed record DDeriv(D Body, D Var, int Order) : D { public override int Prec => 2; }
public sealed record DLimit(D Body, D Var, D Point) : D { public override int Prec => 2; }

public static class Display
{
    private static readonly Dictionary<string, string> FunctionNames = new()
    {
        ["gamma"] = "Γ",
    };

    public static D Build(Expr e) => e switch
    {
        Num n => Number(n.Value),
        Real r => RealNumber(r.Value),
        Sym s => Symbol(s),
        StrExpr str => new DAtom(str.Value, "\"" + str.Value + "\"", "\\text{" + str.Value + "}", AtomKind.Text),
        AddExpr a => Sum(a),
        MulExpr m => Product(m),
        PowExpr p => Power(p),
        FnExpr f => Function(f),
        ListExpr l => l.IsMatrix
            ? new DMatrix(l.Items.Select(row => (IReadOnlyList<D>)((ListExpr)row).Items.Select(Build).ToList()).ToList())
            : new DList(l.Items.Select(Build).ToList()),
        EqExpr q => new DEq(Build(q.Lhs), Build(q.Rhs)),
        _ => new DAtom(e.GetType().Name, e.GetType().Name, e.GetType().Name, AtomKind.Text),
    };

    private static D Number(BigRational v)
    {
        if (v.Sign < 0) return new DNeg(Number(-v));
        if (v.IsInteger) return Int(v.Num.ToString(CultureInfo.InvariantCulture));
        return new DFrac(Int(v.Num.ToString(CultureInfo.InvariantCulture)), Int(v.Den.ToString(CultureInfo.InvariantCulture)));
    }

    private static DAtom Int(string s) => new(s, s, s, AtomKind.Number);

    public static D RealNumber(double v)
    {
        if (double.IsNaN(v)) return new DAtom("Undefined", "Undefined", "\\text{Undefined}", AtomKind.Text);
        if (double.IsInfinity(v)) return v > 0 ? Symbol(Expr.Infinity) : new DNeg(Symbol(Expr.Infinity));
        if (v < 0) return new DNeg(RealNumber(-v));
        var s = v.ToString("G12", CultureInfo.InvariantCulture);
        int ePos = s.IndexOf('E');
        if (ePos >= 0)
        {
            var mant = s[..ePos];
            int exp = int.Parse(s[(ePos + 1)..], CultureInfo.InvariantCulture);
            if (!mant.Contains('.')) mant += ".0";
            return new DSci(mant, exp);
        }
        if (!s.Contains('.')) s += ".0";
        return new DAtom(s, s, s, AtomKind.Number);
    }

    private static D Symbol(Sym s) => s.Name switch
    {
        "Pi" => new DAtom("π", "pi", "\\pi", AtomKind.Constant),
        "E" => new DAtom("e", "e", "e", AtomKind.Constant),
        "I" => new DAtom("i", "I", "i", AtomKind.Constant),
        "Infinity" => new DAtom("∞", "Infinity", "\\infty", AtomKind.Constant),
        "ComplexInfinity" => new DAtom("ComplexInfinity", "ComplexInfinity", "\\tilde{\\infty}", AtomKind.Text),
        "Undefined" => new DAtom("Undefined", "Undefined", "\\text{Undefined}", AtomKind.Text),
        _ => Variable(s.Name),
    };

    private static readonly Dictionary<string, (string Display, string Latex)> Greek = new()
    {
        ["alpha"] = ("α", "\\alpha"), ["beta"] = ("β", "\\beta"), ["gamma"] = ("γ", "\\gamma"), ["delta"] = ("δ", "\\delta"),
        ["epsilon"] = ("ε", "\\varepsilon"), ["zeta"] = ("ζ", "\\zeta"), ["eta"] = ("η", "\\eta"), ["theta"] = ("θ", "\\theta"),
        ["kappa"] = ("κ", "\\kappa"), ["lambda"] = ("λ", "\\lambda"), ["mu"] = ("μ", "\\mu"), ["nu"] = ("ν", "\\nu"),
        ["xi"] = ("ξ", "\\xi"), ["rho"] = ("ρ", "\\rho"), ["sigma"] = ("σ", "\\sigma"), ["tau"] = ("τ", "\\tau"),
        ["phi"] = ("φ", "\\varphi"), ["chi"] = ("χ", "\\chi"), ["psi"] = ("ψ", "\\psi"), ["omega"] = ("ω", "\\omega"),
        ["Gamma"] = ("Γ", "\\Gamma"), ["Delta"] = ("Δ", "\\Delta"), ["Theta"] = ("Θ", "\\Theta"), ["Lambda"] = ("Λ", "\\Lambda"),
        ["Sigma"] = ("Σ", "\\Sigma"), ["Phi"] = ("Φ", "\\Phi"), ["Psi"] = ("Ψ", "\\Psi"), ["Omega"] = ("Ω", "\\Omega"),
    };

    private static DAtom Variable(string name)
    {
        if (Greek.TryGetValue(name, out var g)) return new DAtom(g.Display, name, g.Latex, AtomKind.Variable);
        var latex = name.Length == 1 ? name : "\\mathit{" + name.Replace("_", "\\_") + "}";
        return new DAtom(name, name, latex, AtomKind.Variable);
    }

    /// <summary>Total polynomial degree of a term, used to order sums as x^2 + 2x + 1.</summary>
    private static double Degree(Expr t)
    {
        if (t.IsConstant && t is not FnExpr) return -1000;
        double Factor(Expr f) => f switch
        {
            Sym s when !s.IsSpecial => 1,
            PowExpr { Base: Sym { IsSpecial: false }, Exponent: Num n } => n.Value.ToDouble(),
            PowExpr { Exponent: Num n } p when !p.Base.IsConstant => n.Value.ToDouble() * 0.5,
            _ => 0,
        };
        return t is MulExpr m ? m.Factors.Sum(Factor) : Factor(t);
    }

    /// <summary>Graded lexicographic order on monomials (x^2, x*y, y^2; a*d before b*c), ignoring coefficients.</summary>
    private static int DisplayCompare(Expr a, Expr b)
    {
        var ea = Exponents(a.SplitCoefficient().Term, out var restA);
        var eb = Exponents(b.SplitCoefficient().Term, out var restB);
        foreach (var v in ea.Keys.Union(eb.Keys).OrderBy(s => s, StringComparer.Ordinal))
        {
            var x = ea.GetValueOrDefault(v);
            var y = eb.GetValueOrDefault(v);
            if (x != y) return y.CompareTo(x);
        }
        for (int i = 0; i < Math.Min(restA.Count, restB.Count); i++)
        {
            int c = Expr.Compare(restA[i], restB[i]);
            if (c != 0) return c;
        }
        return restA.Count.CompareTo(restB.Count);
    }

    private static Dictionary<string, double> Exponents(Expr term, out List<Expr> others)
    {
        var map = new Dictionary<string, double>();
        others = [];
        foreach (var f in term is MulExpr m ? m.Factors : [term])
        {
            if (f is Sym { IsSpecial: false } s) map[s.Name] = map.GetValueOrDefault(s.Name) + 1;
            else if (f is PowExpr { Base: Sym { IsSpecial: false } ps, Exponent: Num n }) map[ps.Name] = map.GetValueOrDefault(ps.Name) + n.Value.ToDouble();
            else if (!f.IsOne) others.Add(f);
        }
        return map;
    }

    private static D Sum(AddExpr a)
    {
        var ordered = a.Terms
            .OrderBy(t => t is FnExpr { Name: "O" } ? 1 : 0)
            .ThenByDescending(Degree)
            .ThenBy(t => t.IsNumber ? 0 : 1)
            .ThenBy(t => t, Comparer<Expr>.Create(DisplayCompare))
            .ToList();
        // Avoid a leading minus sign when a positive term of the same degree exists: b^2 - 4ac, sqrt(2) - 1.
        if (ordered[0].HasNegativeCoefficient)
        {
            var d0 = Degree(ordered[0]);
            var pos = ordered.FindIndex(t => !t.HasNegativeCoefficient && Degree(t) == d0 && t is not FnExpr { Name: "O" });
            if (pos > 0)
            {
                var p = ordered[pos];
                ordered.RemoveAt(pos);
                ordered.Insert(0, p);
            }
        }
        var terms = new List<(bool, D)>();
        foreach (var t in ordered)
        {
            if (t.HasNegativeCoefficient) terms.Add((true, Build(-t)));
            else terms.Add((false, Build(t)));
        }
        return new DSum(terms);
    }

    private static D Product(MulExpr m)
    {
        var factors = m.Factors.ToList();
        bool negative = false;
        var num = new List<D>();
        var den = new List<D>();
        if (factors[0] is Num c)
        {
            factors.RemoveAt(0);
            var v = c.Value;
            if (v.Sign < 0) { negative = true; v = -v; }
            if (!v.Num.IsOne) num.Add(Int(v.Num.ToString(CultureInfo.InvariantCulture)));
            if (!v.Den.IsOne) den.Add(Int(v.Den.ToString(CultureInfo.InvariantCulture)));
        }
        else if (factors[0] is Real r)
        {
            factors.RemoveAt(0);
            var v = r.Value;
            if (v < 0) { negative = true; v = -v; }
            if (v != 1) num.Add(RealNumber(v));
        }

        // Polynomial part first, then exponentials and functions, then parenthesised sums: x e^(-x^2) sin(x) (x + 1).
        static int Group(Expr f) => f switch
        {
            Sym { IsSpecial: false } => 0,
            PowExpr { Base: Sym { IsSpecial: false } } => 0,
            Sym => 1,
            PowExpr { Base: AddExpr } => 4,
            PowExpr => 2,
            FnExpr => 3,
            AddExpr => 4,
            _ => 5,
        };
        factors = factors.OrderBy(Group).ToList();
        foreach (var f in factors)
        {
            if (f is PowExpr { Exponent: Num { Value.Sign: < 0 } ne } p)
                den.Add(Build(Expr.Power(p.Base, new Num(-ne.Value))));
            else
                num.Add(Build(f));
        }

        D numerator = num.Count == 0 ? Int("1") : num.Count == 1 ? num[0] : new DProd(num);
        D result = den.Count == 0 ? numerator : new DFrac(numerator, den.Count == 1 ? den[0] : new DProd(den));
        return negative ? new DNeg(result) : result;
    }

    private static D Power(PowExpr p)
    {
        if (p.Exponent is Num { Value: var q })
        {
            if (q == BigRational.Half) return new DSqrt(Build(p.Base), null);
            if (q.Num.IsOne && q.Den > 2 && q.Den < 100) return new DSqrt(Build(p.Base), Int(q.Den.ToString(CultureInfo.InvariantCulture)));
            if (q.Sign < 0) return new DFrac(Int("1"), Build(Expr.Power(p.Base, new Num(-q))));
        }
        return new DPow(Build(p.Base), Build(p.Exponent));
    }

    private static D Function(FnExpr f)
    {
        var a = f.Args;
        switch (f.Name)
        {
            case "abs" when a.Count == 1: return new DAbs(Build(a[0]));
            case "factorial" when a.Count == 1: return new DFactorial(Build(a[0]));
            case "integrate" when a.Count == 2: return new DIntegral(Build(a[0]), Build(a[1]), null, null);
            case "integrate" when a.Count == 4: return new DIntegral(Build(a[0]), Build(a[1]), Build(a[2]), Build(a[3]));
            case "diff" when a.Count == 2: return new DDeriv(Build(a[0]), Build(a[1]), 1);
            case "diff" when a.Count == 3 && a[2] is Num { Value.IsInteger: true } n: return new DDeriv(Build(a[0]), Build(a[1]), (int)n.Value.Num);
            case "sum" when a.Count == 4: return new DBigSum("sum", Build(a[0]), Build(a[1]), Build(a[2]), Build(a[3]));
            case "product" when a.Count == 4: return new DBigSum("product", Build(a[0]), Build(a[1]), Build(a[2]), Build(a[3]));
            case "limit" when a.Count == 3: return new DLimit(Build(a[0]), Build(a[1]), Build(a[2]));
            case "@rel" when a.Count == 3 && a[0] is StrExpr op: return new DEq(Build(a[1]), Build(a[2]), op.Value);
            case "@eq" when a.Count == 2: return new DEq(Build(a[0]), Build(a[1]));
            case "@d" when a.Count == 2 && a[1] is Num { Value.IsInteger: true } order:
                {
                    var n = (int)order.Value.Num;
                    var primes = n <= 3 ? new string('′', n) : $"({n})";
                    var plainPrimes = new string('\'', n);
                    var baseName = a[0].ToString();
                    return new DAtom(baseName + primes, baseName + plainPrimes, baseName + (n <= 3 ? plainPrimes : $"^{{({n})}}"), AtomKind.Variable);
                }
        }
        var name = FunctionNames.GetValueOrDefault(f.Name, f.Name);
        return new DCall(name, f.Name, a.Select(Build).ToList());
    }
}
