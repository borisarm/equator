using System.Globalization;
using System.Numerics;
using System.Text;

namespace Equator.Core.Parsing;

public sealed class ParseException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>One statement of an input cell. <see cref="Suppressed"/> is true when it ended with ';'.</summary>
public sealed record Statement(Expr Tree, bool Suppressed, string Source);

/// <summary>
/// Parses user input into a raw (unevaluated) tree. Operators become inert function nodes such as
/// <c>@plus</c> and <c>@times</c>; the kernel evaluates them in order, which keeps matrix products non-commutative.
/// </summary>
public sealed class Parser
{
    private enum T { Number, Ident, String, Op, NewLine, End }

    private readonly record struct Token(T Kind, string Text, int Pos, bool SpaceBefore);

    private readonly List<Token> _tokens;
    private int _p;
    private readonly string _src;

    private Parser(string src)
    {
        // Text boxes on Windows may use CR or CR LF as line separators.
        src = src.Replace("\r\n", "\n").Replace('\r', '\n');
        _src = src;
        _tokens = Lex(src);
    }

    public static IReadOnlyList<Statement> ParseProgram(string src) => new Parser(src).Program();

    /// <summary>Parses a single expression (convenience for tests and internal use).</summary>
    public static Expr ParseExpr(string src)
    {
        var stmts = ParseProgram(src);
        if (stmts.Count != 1) throw new ParseException("Expected a single expression.", 0);
        return stmts[0].Tree;
    }

    // ---- lexer ----------------------------------------------------------------------------------

    private static readonly string[] MultiOps = [":=", "==", "<=", ">=", "!=", "->", "**", ".*"];

    private static List<Token> Lex(string s)
    {
        var list = new List<Token>();
        int i = 0, depth = 0;
        bool space = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n')
            {
                // Newlines separate statements only outside brackets.
                if (depth == 0) list.Add(new Token(T.NewLine, "\n", i, space));
                i++; space = true; continue;
            }
            if (char.IsWhiteSpace(c)) { i++; space = true; continue; }
            if (c == '#' || (c == '/' && i + 1 < s.Length && s[i + 1] == '/'))
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            int start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i < s.Length && s[i] == '.' && !(i + 1 < s.Length && s[i + 1] == '*'))
                {
                    i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
                    if (j < s.Length && char.IsDigit(s[j]))
                    {
                        i = j;
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                    }
                }
                list.Add(new Token(T.Number, s[start..i], start, space));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new Token(T.Ident, s[start..i], start, space));
            }
            else if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"') sb.Append(s[i++]);
                if (i >= s.Length) throw new ParseException("Unterminated string.", start);
                i++;
                list.Add(new Token(T.String, sb.ToString(), start, space));
            }
            else
            {
                string? op = MultiOps.FirstOrDefault(m => i + m.Length <= s.Length && string.CompareOrdinal(s, i, m, 0, m.Length) == 0);
                if (op is not null) i += op.Length;
                else
                {
                    op = c switch
                    {
                        '−' => "-",
                        '×' or '·' => "*",
                        '÷' => "/",
                        '≤' => "<=",
                        '≥' => ">=",
                        '≠' => "!=",
                        _ => c.ToString(),
                    };
                    i++;
                }
                if ("([{".Contains(op)) depth++;
                if (")]}".Contains(op)) depth = Math.Max(0, depth - 1);
                if (op == "**") op = "^";
                list.Add(new Token(T.Op, op, start, space));
            }
            space = false;
        }
        list.Add(new Token(T.End, "", s.Length, space));
        return list;
    }

    // ---- grammar ------------------------------------------------------------------------------------

    private Token Peek => _tokens[_p];
    private Token Next() => _tokens[_p++];
    private bool IsOp(string op) => Peek.Kind == T.Op && Peek.Text == op;

    private void Expect(string op)
    {
        if (!IsOp(op)) throw Error($"Expected '{op}'" + (Peek.Kind == T.End ? " before end of input." : $" but found '{Peek.Text}'."));
        _p++;
    }

    private ParseException Error(string msg) => new(msg, Peek.Pos);

    private List<Statement> Program()
    {
        var result = new List<Statement>();
        while (true)
        {
            while (Peek.Kind == T.NewLine || IsOp(";")) _p++;
            if (Peek.Kind == T.End) break;
            int start = Peek.Pos;
            var stmt = StatementExpr();
            bool suppressed = false;
            if (IsOp(";")) { suppressed = true; _p++; }
            else if (Peek.Kind == T.NewLine) _p++;
            else if (Peek.Kind != T.End) throw Error($"Unexpected '{Peek.Text}'.");
            int end = _p > 0 ? _tokens[_p - 1].Pos + _tokens[_p - 1].Text.Length : start;
            result.Add(new Statement(stmt, suppressed, _src[start..Math.Min(end, _src.Length)].Trim()));
        }
        return result;
    }

    private Expr StatementExpr()
    {
        var lhs = Equation();
        if (IsOp(":="))
        {
            _p++;
            var rhs = Equation();
            return FnExpr.Inert("@set", lhs, rhs);
        }
        return lhs;
    }

    private Expr Equation()
    {
        var lhs = Sum();
        if (Peek.Kind == T.Op && Peek.Text is "=" or "==" or "<" or ">" or "<=" or ">=" or "!=")
        {
            var op = Next().Text;
            var rhs = Sum();
            if (op == "=") return FnExpr.Inert("@eq", lhs, rhs);
            var rel = FnExpr.Inert("@rel", new StrExpr(op), lhs, rhs);
            // Chained comparisons: a < x <= b.
            while (Peek.Kind == T.Op && Peek.Text is "<" or ">" or "<=" or ">=")
            {
                var op2 = Next().Text;
                rel = FnExpr.Inert("@rel", new StrExpr(op2), rel, Sum());
            }
            return rel;
        }
        if (IsOp("->"))
        {
            _p++;
            return FnExpr.Inert("@eq", lhs, Sum());
        }
        return lhs;
    }

    private Expr Sum()
    {
        var left = Term();
        while (IsOp("+") || IsOp("-"))
        {
            var op = Next().Text;
            var right = Term();
            left = FnExpr.Inert(op == "+" ? "@plus" : "@minus", left, right);
        }
        return left;
    }

    private bool StartsImplicitOperand()
    {
        var t = Peek;
        return t.Kind switch
        {
            T.Number or T.Ident => true,
            T.Op => t.Text is "(" or "{" or "√" or "π" or "∞" || (t.Text == "[" && t.SpaceBefore),
            _ => false,
        };
    }

    private Expr Term()
    {
        var left = Unary();
        while (true)
        {
            if (IsOp("*") || IsOp("/") || IsOp(".*") || IsOp("."))
            {
                var op = Next().Text;
                var right = Unary();
                left = FnExpr.Inert(op switch { "/" => "@div", ".*" => "@etimes", "." => "@dot", _ => "@times" }, left, right);
            }
            else if (StartsImplicitOperand())
            {
                var right = Power();
                left = FnExpr.Inert("@times", left, right);
            }
            else return left;
        }
    }

    private Expr Unary()
    {
        if (IsOp("-")) { _p++; return FnExpr.Inert("@neg", Unary()); }
        if (IsOp("+")) { _p++; return Unary(); }
        return Power();
    }

    private Expr Power()
    {
        var b = Postfix();
        if (IsOp("^"))
        {
            _p++;
            var e = Unary();
            return FnExpr.Inert("@power", b, e);
        }
        return b;
    }

    private Expr Postfix()
    {
        var e = Primary();
        while (true)
        {
            if (IsOp("!")) { _p++; e = FnExpr.Inert("factorial", e); }
            else if (IsOp("'"))
            {
                // f'(x) : derivative of a user function
                int order = 0;
                while (IsOp("'")) { _p++; order++; }
                e = FnExpr.Inert("@prime", e, order);
            }
            else if (IsOp("[") && !Peek.SpaceBefore)
            {
                _p++;
                var idx = ArgList("]");
                e = FnExpr.Inert("@index", [e, .. idx]);
            }
            else if (IsOp("(") && !Peek.SpaceBefore && e is FnExpr { Name: "@prime" })
            {
                _p++;
                var args = ArgList(")");
                e = FnExpr.Inert("@call", [e, .. args]);
            }
            else return e;
        }
    }

    private List<Expr> ArgList(string close)
    {
        var args = new List<Expr>();
        while (Peek.Kind == T.NewLine) _p++;
        if (IsOp(close)) { _p++; return args; }
        while (true)
        {
            args.Add(Equation());
            if (IsOp(",")) { _p++; continue; }
            Expect(close);
            return args;
        }
    }

    private Expr Primary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case T.Number:
                _p++;
                return ParseNumber(t.Text);
            case T.String:
                _p++;
                return new StrExpr(t.Text);
            case T.Ident:
                {
                    _p++;
                    if (IsOp("(") && !Peek.SpaceBefore)
                    {
                        _p++;
                        var args = ArgList(")");
                        return FnExpr.Inert("@call", [new Sym(t.Text), .. args]);
                    }
                    return Identifier(t.Text);
                }
            case T.Op:
                switch (t.Text)
                {
                    case "(":
                        {
                            _p++;
                            var e = Equation();
                            Expect(")");
                            return e;
                        }
                    case "[":
                        _p++;
                        return FnExpr.Inert("@list", ArgList("]").ToArray());
                    case "{":
                        _p++;
                        return FnExpr.Inert("@list", ArgList("}").ToArray());
                    case "%":
                        {
                            _p++;
                            if (Peek.Kind == T.Number && !Peek.SpaceBefore)
                                return FnExpr.Inert("@out", ParseNumber(Next().Text));
                            int back = 1;
                            while (IsOp("%")) { _p++; back++; }
                            return FnExpr.Inert("@out", -back);
                        }
                    case "π": _p++; return Expr.Pi;
                    case "∞": _p++; return Expr.Infinity;
                    case "√":
                        _p++;
                        return FnExpr.Inert("@call", new Sym("sqrt"), Power());
                }
                break;
        }
        if (t.Kind == T.End) throw Error("Unexpected end of input.");
        throw Error($"Unexpected '{t.Text}'.");
    }

    private static Expr Identifier(string name) => name switch
    {
        "pi" or "Pi" or "PI" => Expr.Pi,
        "e" or "E" => Expr.E,
        "I" => Expr.I,
        "oo" or "inf" or "Inf" or "infinity" or "Infinity" => Expr.Infinity,
        "Undefined" or "nan" or "NaN" => Expr.Undefined,
        "ComplexInfinity" or "zoo" => Expr.ComplexInfinity,
        "true" or "True" => Expr.One,
        "false" or "False" => Expr.Zero,
        _ => new Sym(name),
    };

    private static Expr ParseNumber(string text)
    {
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            // More significant digits than a double holds: keep them all as an arbitrary-precision number.
            var mantissa = text.Split('e', 'E')[0].Replace(".", "").TrimStart('0');
            if (mantissa.Length > 17)
                return new Real(Numerics.BigFloat.Parse(text, Numerics.BigFloat.DigitsToBits(mantissa.Length) + 16), mantissa.Length);
            return new Real(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        return new Num(BigInteger.Parse(text, CultureInfo.InvariantCulture));
    }
}
