using System.Text;

namespace Equator.Core.Printing;

/// <summary>LaTeX output, suitable for pasting into documents.</summary>
public static class LatexFormatter
{
    private static readonly HashSet<string> KnownLatexFunctions =
    [
        "sin", "cos", "tan", "cot", "sec", "csc", "sinh", "cosh", "tanh", "coth", "log", "exp", "arg", "det", "gcd", "max", "min",
    ];

    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["asin"] = "\\arcsin", ["acos"] = "\\arccos", ["atan"] = "\\arctan", ["log"] = "\\ln", ["gamma"] = "\\Gamma",
    };

    public static string Format(Expr e) => Format(Display.Build(e));

    public static string Format(D d)
    {
        var sb = new StringBuilder();
        Write(sb, d);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, D d, int minPrec = 0)
    {
        bool paren = d.Prec < minPrec;
        if (paren) sb.Append("\\left(");
        switch (d)
        {
            case DAtom a: sb.Append(a.Latex); break;
            case DSum s:
                for (int i = 0; i < s.Terms.Count; i++)
                {
                    var (neg, t) = s.Terms[i];
                    if (i == 0) { if (neg) sb.Append('-'); }
                    else sb.Append(neg ? " - " : " + ");
                    Write(sb, t, 2);
                }
                break;
            case DNeg n:
                sb.Append('-');
                Write(sb, n.Inner, 3);
                break;
            case DProd p:
                for (int i = 0; i < p.Factors.Count; i++)
                {
                    // Juxtaposed numbers need an explicit multiplication sign.
                    if (i > 0) sb.Append(StartsWithDigit(p.Factors[i]) ? " \\cdot " : " \\, ");
                    Write(sb, p.Factors[i], 4);
                }
                break;
            case DFrac f:
                sb.Append("\\frac{");
                Write(sb, f.Num);
                sb.Append("}{");
                Write(sb, f.Den);
                sb.Append('}');
                break;
            case DPow p:
                if (p.Base is DCall call && call.Args.Count == 1 && KnownLatexFunctions.Contains(call.Plain) && p.Exp is DAtom)
                {
                    // sin^2(x) convention
                    sb.Append(FunctionName(call)).Append("^{");
                    Write(sb, p.Exp);
                    sb.Append("}\\left(");
                    Write(sb, call.Args[0]);
                    sb.Append("\\right)");
                    break;
                }
                sb.Append('{');
                Write(sb, p.Base, 6);
                sb.Append("}^{");
                Write(sb, p.Exp);
                sb.Append('}');
                break;
            case DSci s:
                sb.Append(s.Mantissa).Append(" \\times 10^{").Append(s.Exponent).Append('}');
                break;
            case DSqrt r:
                sb.Append("\\sqrt");
                if (r.Index is not null) { sb.Append('['); Write(sb, r.Index); sb.Append(']'); }
                sb.Append('{');
                Write(sb, r.Arg);
                sb.Append('}');
                break;
            case DCall c:
                sb.Append(FunctionName(c)).Append("\\left(");
                WriteList(sb, c.Args);
                sb.Append("\\right)");
                break;
            case DAbs a:
                sb.Append("\\left|");
                Write(sb, a.Inner);
                sb.Append("\\right|");
                break;
            case DFactorial f:
                Write(sb, f.Inner, 7);
                sb.Append('!');
                break;
            case DList l:
                sb.Append("\\left[");
                WriteList(sb, l.Items);
                sb.Append("\\right]");
                break;
            case DMatrix m:
                sb.Append("\\begin{pmatrix}");
                for (int i = 0; i < m.Rows.Count; i++)
                {
                    if (i > 0) sb.Append(" \\\\ ");
                    for (int j = 0; j < m.Rows[i].Count; j++)
                    {
                        if (j > 0) sb.Append(" & ");
                        Write(sb, m.Rows[i][j]);
                    }
                }
                sb.Append("\\end{pmatrix}");
                break;
            case DEq q:
                Write(sb, q.Lhs, q.Lhs is DEq ? 0 : 1);
                sb.Append(q.Op switch { "<=" => " \\le ", ">=" => " \\ge ", "!=" => " \\ne ", var o => " " + o + " " });
                Write(sb, q.Rhs, 1);
                break;
            case DIntegral i:
                sb.Append("\\int");
                if (i.Lower is not null)
                {
                    sb.Append("_{"); Write(sb, i.Lower); sb.Append("}^{"); Write(sb, i.Upper!); sb.Append('}');
                }
                sb.Append(' ');
                Write(sb, i.Body, 2);
                sb.Append(" \\, d");
                Write(sb, i.Var);
                break;
            case DBigSum s:
                sb.Append(s.Op == "sum" ? "\\sum" : "\\prod").Append("_{");
                Write(sb, s.Var); sb.Append('='); Write(sb, s.Lower);
                sb.Append("}^{"); Write(sb, s.Upper); sb.Append("} ");
                Write(sb, s.Body, 2);
                break;
            case DDeriv dv:
                if (dv.Order == 1) { sb.Append("\\frac{d}{d"); Write(sb, dv.Var); sb.Append("} "); }
                else { sb.Append("\\frac{d^{").Append(dv.Order).Append("}}{d"); Write(sb, dv.Var); sb.Append("^{").Append(dv.Order).Append("}} "); }
                Write(sb, dv.Body, 4);
                break;
            case DLimit lm:
                sb.Append("\\lim_{");
                Write(sb, lm.Var); sb.Append(" \\to "); Write(sb, lm.Point);
                sb.Append("} ");
                Write(sb, lm.Body, 2);
                break;
        }
        if (paren) sb.Append("\\right)");
    }

    private static string FunctionName(DCall c)
    {
        if (Renamed.TryGetValue(c.Plain, out var r)) return r;
        if (KnownLatexFunctions.Contains(c.Plain)) return "\\" + c.Plain;
        return "\\operatorname{" + c.Plain + "}";
    }

    private static bool StartsWithDigit(D d) => d switch
    {
        DAtom a => a.Kind == AtomKind.Number,
        DPow p => StartsWithDigit(p.Base),
        DSci => true,
        DFrac => false,
        _ => false,
    };

    private static void WriteList(StringBuilder sb, IReadOnlyList<D> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Write(sb, items[i]);
        }
    }
}
