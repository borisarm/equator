using System.Text;

namespace Equator.Core.Printing;

/// <summary>Linear text form. The output can be parsed back by <see cref="Parsing.Parser"/>.</summary>
public static class TextFormatter
{
    public static string Format(Expr e) => Format(Display.Build(e));

    public static string Format(D d)
    {
        var sb = new StringBuilder();
        Write(sb, d);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, D d, int minPrec = 0)
    {
        bool paren = (d is DIntegral or DBigSum or DDeriv or DLimit ? 10 : d.Prec) < minPrec;
        if (paren) sb.Append('(');
        switch (d)
        {
            case DAtom a: sb.Append(a.Plain); break;
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
                    if (i > 0) sb.Append('*');
                    Write(sb, p.Factors[i], 4);
                }
                break;
            case DFrac f:
                Write(sb, f.Num, 3);
                sb.Append('/');
                Write(sb, f.Den, 4);
                break;
            case DPow p:
                Write(sb, p.Base, 6);
                sb.Append('^');
                Write(sb, p.Exp, 7);
                break;
            case DSci s:
                sb.Append(s.Mantissa).Append('e').Append(s.Exponent);
                break;
            case DSqrt r:
                if (r.Index is null) { sb.Append("sqrt("); Write(sb, r.Arg); sb.Append(')'); }
                else { Write(sb, r.Arg, 6); sb.Append("^(1/"); Write(sb, r.Index); sb.Append(')'); }
                break;
            case DCall c:
                sb.Append(c.Plain).Append('(');
                WriteList(sb, c.Args);
                sb.Append(')');
                break;
            case DAbs a:
                sb.Append("abs(");
                Write(sb, a.Inner);
                sb.Append(')');
                break;
            case DFactorial f:
                Write(sb, f.Inner, 7);
                sb.Append('!');
                break;
            case DList l:
                sb.Append('[');
                WriteList(sb, l.Items);
                sb.Append(']');
                break;
            case DMatrix m:
                sb.Append('[');
                for (int i = 0; i < m.Rows.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('[');
                    WriteList(sb, m.Rows[i]);
                    sb.Append(']');
                }
                sb.Append(']');
                break;
            case DEq q:
                Write(sb, q.Lhs, 1);
                sb.Append(' ').Append(q.Op).Append(' ');
                Write(sb, q.Rhs, 1);
                break;
            case DIntegral i:
                sb.Append("integrate(");
                Write(sb, i.Body); sb.Append(", "); Write(sb, i.Var);
                if (i.Lower is not null) { sb.Append(", "); Write(sb, i.Lower); sb.Append(", "); Write(sb, i.Upper!); }
                sb.Append(')');
                break;
            case DBigSum s:
                sb.Append(s.Op).Append('(');
                WriteList(sb, [s.Body, s.Var, s.Lower, s.Upper]);
                sb.Append(')');
                break;
            case DDeriv dv:
                sb.Append("diff(");
                Write(sb, dv.Body); sb.Append(", "); Write(sb, dv.Var);
                if (dv.Order != 1) sb.Append(", ").Append(dv.Order);
                sb.Append(')');
                break;
            case DLimit lm:
                sb.Append("limit(");
                WriteList(sb, [lm.Body, lm.Var, lm.Point]);
                sb.Append(')');
                break;
        }
        if (paren) sb.Append(')');
    }

    private static void WriteList(StringBuilder sb, IReadOnlyList<D> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Write(sb, items[i]);
        }
    }
}
