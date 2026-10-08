namespace Equator.Core.Printing;

/// <summary>
/// Two-dimensional typesetting model consumed by the UI renderer. Boxes describe structure only
/// (fractions, scripts, radicals, fences); fonts and metrics are the renderer's concern.
/// </summary>
public abstract record Box;

public enum RunKind { Number, Variable, Function, Operator, Punctuation, Text, BigOperator }

public sealed record RunBox(string Text, RunKind Kind) : Box;
public sealed record RowBox(IReadOnlyList<Box> Items) : Box;
public sealed record FracBox(Box Num, Box Den) : Box;
public sealed record ScriptBox(Box Base, Box? Sup, Box? Sub) : Box;
public sealed record RadicalBox(Box Inner, Box? Index) : Box;
public sealed record FenceBox(Box Inner, char Open, char Close) : Box;
public sealed record GridBox(IReadOnlyList<IReadOnlyList<Box>> Rows) : Box;

/// <summary>Large operator (∫, Σ, Π, lim) with optional limits placed under/over or as scripts.</summary>
public sealed record BigOpBox(string Symbol, Box? Lower, Box? Upper, bool LimitsUnder) : Box;

public static class BoxFormatter
{
    /// <summary>Conventional typeset names for inverse functions.</summary>
    private static readonly Dictionary<string, string> TypesetNames = new()
    {
        ["asin"] = "arcsin", ["acos"] = "arccos", ["atan"] = "arctan", ["acot"] = "arccot", ["asec"] = "arcsec", ["acsc"] = "arccsc",
        ["asinh"] = "arsinh", ["acosh"] = "arcosh", ["atanh"] = "artanh", ["log"] = "ln",
    };

    public static Box Format(Expr e) => Build(Display.Build(e));

    public static Box Build(D d, int minPrec = 0)
    {
        var box = BuildCore(d);
        return d.Prec < minPrec ? new FenceBox(box, '(', ')') : box;
    }

    private static Box Op(string s) => new RunBox(s, RunKind.Operator);
    private static Box Punct(string s) => new RunBox(s, RunKind.Punctuation);

    private static Box BuildCore(D d)
    {
        switch (d)
        {
            case DAtom a:
                return new RunBox(a.Display, a.Kind switch
                {
                    AtomKind.Number => RunKind.Number,
                    AtomKind.Variable => RunKind.Variable,
                    AtomKind.Constant => a.Display.Length == 1 && char.IsLetter(a.Display[0]) ? RunKind.Variable : RunKind.Number,
                    _ => RunKind.Text,
                });
            case DSum s:
                {
                    var items = new List<Box>();
                    for (int i = 0; i < s.Terms.Count; i++)
                    {
                        var (neg, t) = s.Terms[i];
                        if (i == 0) { if (neg) items.Add(new RunBox("−", RunKind.Punctuation)); }
                        else items.Add(Op(neg ? "−" : "+"));
                        items.Add(Build(t, 2));
                    }
                    return new RowBox(items);
                }
            case DNeg n:
                return new RowBox([new RunBox("−", RunKind.Punctuation), Build(n.Inner, 3)]);
            case DProd p:
                {
                    var items = new List<Box>();
                    for (int i = 0; i < p.Factors.Count; i++)
                    {
                        var f = p.Factors[i];
                        // Two adjacent numbers need a visible multiplication sign.
                        if (i > 0 && StartsWithNumber(f)) items.Add(Op("×"));
                        else if (i > 0) items.Add(new RunBox(" ", RunKind.Punctuation));
                        items.Add(Build(f, 4));
                    }
                    return new RowBox(items);
                }
            case DFrac f:
                return new FracBox(Build(f.Num), Build(f.Den));
            case DPow p:
                {
                    if (p.Base is DCall call && call.Args.Count == 1 && p.Exp is DAtom { Kind: AtomKind.Number } && IsTrig(call.Plain))
                    {
                        // sin²(x) convention
                        return new RowBox([new ScriptBox(new RunBox(call.Name, RunKind.Function), Build(p.Exp), null),
                            new FenceBox(Build(call.Args[0]), '(', ')')]);
                    }
                    return new ScriptBox(Build(p.Base, 6), Build(p.Exp), null);
                }
            case DSci s:
                return new RowBox([new RunBox(s.Mantissa, RunKind.Number), Op("×"),
                    new ScriptBox(new RunBox("10", RunKind.Number), new RunBox(s.Exponent < 0 ? "−" + (-s.Exponent) : s.Exponent.ToString(), RunKind.Number), null)]);
            case DSqrt r:
                return new RadicalBox(Build(r.Arg), r.Index is null ? null : Build(r.Index));
            case DCall c:
                return new RowBox([new RunBox(TypesetNames.GetValueOrDefault(c.Name, c.Name), RunKind.Function), new FenceBox(CommaList(c.Args), '(', ')')]);
            case DAbs a:
                return new FenceBox(Build(a.Inner), '|', '|');
            case DFactorial f:
                return new RowBox([Build(f.Inner, 7), Punct("!")]);
            case DList l:
                return new FenceBox(CommaList(l.Items), '{', '}');
            case DMatrix m:
                return new FenceBox(new GridBox(m.Rows.Select(r => (IReadOnlyList<Box>)r.Select(x => Build(x)).ToList()).ToList()), '(', ')');
            case DEq q:
                return new RowBox([Build(q.Lhs, q.Lhs is DEq ? 0 : 1), Op(q.Op switch { "<=" => "≤", ">=" => "≥", "!=" => "≠", var o => o }), Build(q.Rhs, 1)]);
            case DIntegral i:
                return new RowBox([
                    new BigOpBox("∫", i.Lower is null ? null : Build(i.Lower), i.Upper is null ? null : Build(i.Upper), false),
                    Build(i.Body, 2),
                    new RunBox(" d", RunKind.Function),
                    Build(i.Var),
                ]);
            case DBigSum s:
                return new RowBox([
                    new BigOpBox(s.Op == "sum" ? "∑" : "∏", new RowBox([Build(s.Var), Op("="), Build(s.Lower)]), Build(s.Upper), true),
                    Build(s.Body, 2),
                ]);
            case DDeriv dv:
                {
                    Box num = dv.Order == 1 ? new RunBox("d", RunKind.Function) : new ScriptBox(new RunBox("d", RunKind.Function), new RunBox(dv.Order.ToString(), RunKind.Number), null);
                    Box den = dv.Order == 1
                        ? new RowBox([new RunBox("d", RunKind.Function), Build(dv.Var)])
                        : new RowBox([new RunBox("d", RunKind.Function), new ScriptBox(Build(dv.Var), new RunBox(dv.Order.ToString(), RunKind.Number), null)]);
                    return new RowBox([new FracBox(num, den), Build(dv.Body, 4)]);
                }
            case DLimit lm:
                return new RowBox([
                    new BigOpBox("lim", new RowBox([Build(lm.Var), Op("→"), Build(lm.Point)]), null, true),
                    Build(lm.Body, 2),
                ]);
        }
        return new RunBox("?", RunKind.Text);
    }

    private static bool IsTrig(string name) => name is "sin" or "cos" or "tan" or "cot" or "sec" or "csc" or "sinh" or "cosh" or "tanh";

    private static bool StartsWithNumber(D d) => d switch
    {
        DAtom a => a.Kind == AtomKind.Number,
        DPow p => StartsWithNumber(p.Base),
        DSci => true,
        _ => false,
    };

    private static Box CommaList(IReadOnlyList<D> items)
    {
        var list = new List<Box>();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) list.Add(Punct(", "));
            list.Add(Build(items[i]));
        }
        return new RowBox(list);
    }
}
