using System.Text;

namespace Equator.Core.Parsing;

/// <summary>
/// Translates LaTeX mathematics (as produced by handwriting recognition) into Equator input syntax.
/// Rows of stacked environments (matrix, cases, aligned, ...) and implication arrows (⇒, ∴) start new
/// lines, so the result is a list of lines. Separators between alternatives are written " or ", " and "
/// or " , " at the top level of a line (see <see cref="Checking.WorkChecker"/>).
/// </summary>
public static class LatexConverter
{
    public static List<string> Convert(string latex)
    {
        var tokens = Tokenize(latex);
        var parser = new Converter(tokens);
        return parser.Document().Select(Tidy).Where(l => l.Length > 0).ToList();
    }

    /// <summary>Single-line convenience: the lines joined (rows of an environment become separate lines).</summary>
    public static string ConvertLine(string latex) => string.Join("\n", Convert(latex));

    private static string Tidy(string line)
    {
        var s = line.Trim();
        // Trailing punctuation from the end of a sentence.
        while (s.Length > 0 && (s.EndsWith('.') && !char.IsDigit(s.Length > 1 ? s[^2] : ' ') || s.EndsWith(',') || s.EndsWith(';')))
            s = s[..^1].TrimEnd();
        while (s.StartsWith(" , ") || s.StartsWith(",")) s = s.TrimStart(' ', ',');
        return s;
    }

    // ---- tokens ----------------------------------------------------------------------------------

    private enum K { Cmd, Char, End }
    private readonly record struct Tok(K Kind, string Text);

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\\')
            {
                if (i + 1 < s.Length && !char.IsLetter(s[i + 1]))
                {
                    list.Add(new Tok(K.Cmd, s[i + 1].ToString()));
                    i += 2;
                    continue;
                }
                int j = i + 1;
                while (j < s.Length && char.IsLetter(s[j])) j++;
                list.Add(new Tok(K.Cmd, s[(i + 1)..j]));
                i = j;
                continue;
            }
            // Unicode symbols that sometimes appear directly.
            var mapped = c switch
            {
                '−' => "-", '×' or '·' or '⋅' => "\\cdot", '÷' => "\\div", '≤' => "\\le", '≥' => "\\ge", '≠' => "\\ne",
                '±' => "\\pm", '∞' => "\\infty", 'π' => "\\pi", '√' => "\\sqrt", '∫' => "\\int", '⇒' or '→' => "\\Rightarrow",
                _ => null,
            };
            if (mapped is not null) { list.Add(mapped.StartsWith('\\') ? new Tok(K.Cmd, mapped[1..]) : new Tok(K.Char, mapped)); i++; continue; }
            list.Add(new Tok(K.Char, c.ToString()));
            i++;
        }
        list.Add(new Tok(K.End, ""));
        return list;
    }

    // ---- translation ---------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> FunctionCommands = new()
    {
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["cot"] = "cot", ["sec"] = "sec", ["csc"] = "csc",
        ["arcsin"] = "asin", ["arccos"] = "acos", ["arctan"] = "atan", ["sinh"] = "sinh", ["cosh"] = "cosh",
        ["tanh"] = "tanh", ["coth"] = "coth", ["ln"] = "log", ["log"] = "log", ["lg"] = "log10", ["exp"] = "exp",
        ["arsinh"] = "asinh", ["arcosh"] = "acosh", ["artanh"] = "atanh", ["sgn"] = "sign", ["max"] = "max", ["min"] = "min",
        ["det"] = "det", ["gcd"] = "gcd",
    };

    private static readonly HashSet<string> PlainFunctionNames =
        ["sin", "cos", "tan", "cot", "sec", "csc", "ln", "log", "exp", "sinh", "cosh", "tanh", "arcsin", "arccos", "arctan", "sqrt"];

    private static readonly HashSet<string> Greek =
    [
        "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta", "theta", "vartheta", "iota", "kappa",
        "lambda", "mu", "nu", "xi", "rho", "sigma", "tau", "upsilon", "phi", "varphi", "chi", "psi", "omega",
        "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Phi", "Psi", "Omega",
    ];

    private sealed class Converter(List<Tok> toks)
    {
        private int _p;
        private Tok Peek => toks[_p];
        private Tok PeekAt(int k) => toks[Math.Min(_p + k, toks.Count - 1)];
        private Tok Next() => toks[_p++];
        private bool IsChar(string c, int k = 0) => PeekAt(k) is { Kind: K.Char } t && t.Text == c;
        private bool IsCmd(string c, int k = 0) => PeekAt(k) is { Kind: K.Cmd } t && t.Text == c;

        /// <summary>The whole input: a list of lines.</summary>
        public List<string> Document()
        {
            var lines = new List<string>();
            var current = new StringBuilder();
            while (Peek.Kind != K.End)
            {
                var seq = Sequence(StopAtLineBreak);
                // Rows of environments come before the rest of the line.
                if (seq.ExtraLines.Count > 0)
                {
                    if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }
                    lines.AddRange(seq.ExtraLines);
                }
                current.Append(seq.Text);
                if (Peek.Kind == K.End) break;
                var t = Next();
                if (t.Kind == K.Cmd && (t.Text is "\\" or "Rightarrow" or "implies" or "therefore" or "iff" or "Leftrightarrow" or "Longrightarrow" or "Longleftrightarrow" or "cr" or "newline"))
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                else if (t.Kind == K.Char && t.Text is "}" ) { /* stray closing brace */ }
                else if (t.Kind == K.Cmd && t.Text == "right") SkipDelimiter();
                else if (t.Kind == K.Char && t.Text == "&") { }
            }
            lines.Add(current.ToString());
            return lines;
        }

        private bool StopAtLineBreak(Tok t) =>
            t.Kind == K.Cmd && t.Text is "\\" or "Rightarrow" or "implies" or "therefore" or "iff" or "Leftrightarrow" or "Longrightarrow" or "Longleftrightarrow" or "cr" or "newline" or "right"
            || t.Kind == K.Char && t.Text is "}" or "&";

        private sealed class Seq
        {
            public readonly List<(bool Operand, string Text)> Items = [];
            public readonly List<string> ExtraLines = [];
            public string? Differential;
            public string Text => Join(Items);
        }

        /// <summary>Joins items, inserting explicit products between adjacent operands.</summary>
        private static string Join(List<(bool Operand, string Text)> items)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0 && items[i].Operand && items[i - 1].Operand)
                {
                    // 2x, 3sin(x): a number followed by a name reads naturally without the star.
                    var prev = items[i - 1].Text;
                    var next = items[i].Text;
                    bool number = prev.All(ch => char.IsDigit(ch) || ch == '.');
                    bool juxtapose = number && (char.IsLetter(next[0]) || next[0] == '(')
                        || prev.EndsWith(')') && prev.StartsWith('(') && next.StartsWith('(');
                    if (!juxtapose) sb.Append('*');
                }
                sb.Append(items[i].Text);
            }
            return sb.ToString();
        }

        /// <summary>Reads items until a token satisfying stop (not consumed) or the end.</summary>
        private Seq Sequence(Func<Tok, bool> stop, bool integrand = false)
        {
            var seq = new Seq();
            while (Peek.Kind != K.End && !stop(Peek))
            {
                // The differential that ends an integrand: d x, \mathrm{d} x, \, dx.
                if (integrand && Differential() is { } dv) { seq.Differential = dv; break; }
                Item(seq, integrand);
                if (integrand && seq.Differential is not null) break;
            }
            return seq;
        }

        private string? Differential()
        {
            int save = _p;
            bool d = false;
            if (IsChar("d")) { Next(); d = true; }
            else if ((IsCmd("mathrm") || IsCmd("text") || IsCmd("operatorname")) && IsChar("{", 1) && IsChar("d", 2) && IsChar("}", 3)) { _p += 4; d = true; }
            if (d && Peek is { Kind: K.Char } v && v.Text.Length == 1 && char.IsLetter(v.Text[0]))
            {
                Next();
                // A differential is followed by the end of the integrand, not by more factors.
                if (Peek.Kind == K.End || IsChar("+") || IsChar("-") || IsChar("=") || IsChar("}") || IsChar(")") || IsChar(",")
                    || IsChar("&") || IsCmd("\\") || IsCmd("right") || IsCmd("int") || IsChar("<") || IsChar(">") || IsCmd(",") || IsCmd("quad")
                    || IsChar(".") || IsCmd("Rightarrow") || IsCmd("le") || IsCmd("ge") || IsCmd("leq") || IsCmd("geq"))
                    return v.Text;
            }
            _p = save;
            return null;
        }

        private void Operand(Seq seq, string text) => seq.Items.Add((true, text));
        private void Operator(Seq seq, string text) => seq.Items.Add((false, text));

        private void Item(Seq seq, bool integrand)
        {
            var t = Next();
            if (t.Kind == K.Char)
            {
                var c = t.Text[0];
                if (char.IsDigit(c) || (c == '.' && Peek is { Kind: K.Char } nd && char.IsDigit(nd.Text[0])))
                {
                    var sb = new StringBuilder(t.Text);
                    while (Peek is { Kind: K.Char } d && (char.IsDigit(d.Text[0]) || d.Text == "." && PeekAt(1) is { Kind: K.Char } after && char.IsDigit(after.Text[0])))
                        sb.Append(Next().Text);
                    // A decimal comma: 3{,}5
                    PostfixAndOperand(seq, sb.ToString());
                    return;
                }
                if (char.IsLetter(c))
                {
                    // Plain function names written without a backslash (sin x, log x).
                    foreach (var name in PlainFunctionNames.OrderByDescending(n => n.Length))
                        if (Spells(t.Text + RestLetters(name.Length - 1), name))
                        {
                            _p += name.Length - 1;
                            FunctionApplication(seq, FunctionCommands.GetValueOrDefault(name, name));
                            return;
                        }
                    // "or" / "and" as words between alternatives.
                    if (Spells(t.Text + RestLetters(1), "or") && WordEndsAt(1)) { _p += 1; Operator(seq, " or "); return; }
                    if (Spells(t.Text + RestLetters(2), "and") && WordEndsAt(2)) { _p += 2; Operator(seq, " and "); return; }
                    var ident = Subscripted(t.Text);
                    // Single-letter function names: f(x), g(t), y(0) and their derivatives f'(x).
                    var primes = Primes();
                    if (IsChar("(") && (ident is "f" or "g" or "h" or "F" or "G" or "H" || (ident is "y" or "u" or "v" && SimpleParenthesisedArgument())))
                    {
                        Next();
                        var arg = Sequence(tk => tk.Kind == K.Char && tk.Text == ")");
                        if (IsChar(")")) Next();
                        PostfixAndOperand(seq, ident + primes + "(" + arg.Text + ")");
                        return;
                    }
                    PostfixAndOperand(seq, ident + primes);
                    return;
                }
                switch (c)
                {
                    case '{':
                        {
                            var inner = Sequence(tk => tk.Kind == K.Char && tk.Text == "}", integrand);
                            if (IsChar("}")) Next();
                            if (inner.Differential is not null) seq.Differential = inner.Differential;
                            if (inner.Items.Count > 0) PostfixAndOperand(seq, "(" + inner.Text + ")");
                            return;
                        }
                    case '(':
                    case '[':
                        {
                            var close = c == '(' ? ")" : "]";
                            var inner = Sequence(tk => tk.Kind == K.Char && (tk.Text == close || tk.Text == ")" || tk.Text == "]"));
                            if (Peek.Kind == K.Char && Peek.Text is ")" or "]") Next();
                            PostfixAndOperand(seq, "(" + inner.Text + ")");
                            return;
                        }
                    case '|':
                        {
                            var inner = Sequence(tk => tk.Kind == K.Char && tk.Text == "|");
                            if (IsChar("|")) Next();
                            PostfixAndOperand(seq, "abs(" + inner.Text + ")");
                            return;
                        }
                    case '+': case '-': case '*': case '/': case '=': case '<': case '>':
                        if (c == '<' && IsChar("=")) { Next(); Operator(seq, "<="); return; }
                        if (c == '>' && IsChar("=")) { Next(); Operator(seq, ">="); return; }
                        bool binary = seq.Items.Count > 0 && seq.Items[^1].Operand;
                        Operator(seq, c == '=' ? " = " : c is '<' or '>' ? $" {c} " : c is '+' or '-' && binary ? $" {c} " : t.Text);
                        return;
                    case ',':
                        // Decimal comma between digits ("3{,}5") is handled by the group; here it separates items.
                        Operator(seq, " , ");
                        return;
                    case ';':
                        Operator(seq, " , ");
                        return;
                    case '!':
                        if (seq.Items.Count > 0 && seq.Items[^1].Operand) { seq.Items[^1] = (true, seq.Items[^1].Text + "!"); return; }
                        return;
                    case '\'':
                        if (seq.Items.Count > 0 && seq.Items[^1].Operand) seq.Items[^1] = (true, seq.Items[^1].Text + "'");
                        return;
                    case '^': case '_':
                        // Scripts with nothing in front: attach to an empty base.
                        Script();
                        return;
                    case ':': case '.': case '~': case '`': case '"': case '?': case '%': case '&':
                        return;
                }
                return;
            }
            if (t.Kind == K.Cmd) Command(t.Text, seq, integrand);
        }

        /// <summary>
        /// A word such as "or" ends at offset k when no letter follows, or when the next letter starts a new
        /// relation ("x = 2 or x = 3" arrives without spaces as ...2orx=3).
        /// </summary>
        private bool WordEndsAt(int k)
        {
            if (!IsLetterAt(k)) return true;
            int j = k + 1;
            if (IsChar("_", j)) j += IsChar("{", j + 1) ? 4 : 2;
            return IsChar("=", j) || IsChar("<", j) || IsChar(">", j) || IsCmd("le", j) || IsCmd("ge", j) || IsCmd("leq", j) || IsCmd("geq", j) || IsCmd("neq", j) || IsCmd("ne", j);
        }

        private bool IsLetterAt(int k) => PeekAt(k) is { Kind: K.Char } t && t.Text.Length == 1 && char.IsLetter(t.Text[0]);

        private string RestLetters(int n)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < n; k++)
                if (PeekAt(k) is { Kind: K.Char } t && t.Text.Length == 1 && char.IsLetter(t.Text[0])) sb.Append(t.Text);
                else break;
            return sb.ToString();
        }

        private static bool Spells(string s, string name) => s == name;

        private bool SimpleParenthesisedArgument()
        {
            // ( number ) or ( letter ): y(0), y(x).
            if (!IsChar("(")) return false;
            int k = 1;
            if (IsChar("-", k)) k++;
            var t = PeekAt(k);
            if (t.Kind != K.Char) return false;
            if (char.IsDigit(t.Text[0]))
            {
                while (PeekAt(k) is { Kind: K.Char } d && (char.IsDigit(d.Text[0]) || d.Text == ".")) k++;
                return IsChar(")", k);
            }
            return char.IsLetter(t.Text[0]) && IsChar(")", k + 1);
        }

        private string Primes()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (IsChar("'")) { Next(); sb.Append('\''); continue; }
                if (IsChar("^") && IsCmd("prime", 1)) { _p += 2; sb.Append('\''); continue; }
                if (IsChar("^") && IsChar("{", 1) && IsCmd("prime", 2))
                {
                    _p += 2;
                    while (IsCmd("prime")) { Next(); sb.Append('\''); }
                    if (IsChar("}")) Next();
                    continue;
                }
                return sb.ToString();
            }
        }

        /// <summary>x_1, x_{12}, S_2 → x_1, x_12, S_2 (subscripts become part of the name).</summary>
        private string Subscripted(string name)
        {
            if (!IsChar("_")) return name;
            Next();
            var sub = ScriptArgument(raw: true);
            var clean = new string(sub.Where(char.IsLetterOrDigit).ToArray());
            return clean.Length == 0 ? name : name + "_" + clean;
        }

        /// <summary>Adds an operand, then its superscripts, primes, factorials and degree signs.</summary>
        private void PostfixAndOperand(Seq seq, string operand)
        {
            while (true)
            {
                if (IsChar("^"))
                {
                    Next();
                    if (IsChar("{") && IsCmd("circ", 1) && IsChar("}", 2)) { _p += 3; operand = "(" + operand + "*pi/180)"; continue; }
                    if (IsCmd("circ")) { Next(); operand = "(" + operand + "*pi/180)"; continue; }
                    if (IsCmd("prime")) { Next(); operand += "'"; continue; }
                    var exp = ScriptArgument();
                    operand = Atomic(operand) + "^" + (IsSimpleExponent(exp) ? exp : "(" + exp + ")");
                    continue;
                }
                if (IsChar("_")) { Next(); ScriptArgument(); continue; } // subscripts on non-names are dropped
                if (IsChar("!")) { Next(); operand = Atomic(operand) + "!"; continue; }
                break;
            }
            Operand(seq, operand);
        }

        private static bool IsSimpleExponent(string e) =>
            e.Length > 0 && (e.All(char.IsDigit) || e.Length == 1 && char.IsLetter(e[0]));

        private static string Atomic(string s)
        {
            if (s.Length == 0) return s;
            if (s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '\'')) return s;
            if (s[0] == '(' && Matching(s, 0) == s.Length - 1) return s;
            // name(...) calls are atomic too.
            int paren = s.IndexOf('(');
            if (paren > 0 && s[..paren].All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '\'') && Matching(s, paren) == s.Length - 1) return s;
            return "(" + s + ")";
        }

        private static int Matching(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] is '(' or '[') depth++;
                else if (s[i] is ')' or ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>The argument of ^ or _: a braced group or a single token.</summary>
        private string ScriptArgument(bool raw = false)
        {
            if (IsChar("{"))
            {
                Next();
                var inner = Sequence(tk => tk.Kind == K.Char && tk.Text == "}");
                if (IsChar("}")) Next();
                return inner.Text;
            }
            var t = Next();
            if (t.Kind == K.Char) return t.Text;
            var seq = new Seq();
            Command(t.Text, seq, false);
            return seq.Text;
        }

        private void Script() => ScriptArgument();

        /// <summary>A braced argument {...} (or a single token) as Equator text.</summary>
        private Seq Group(bool integrand = false)
        {
            if (IsChar("{"))
            {
                Next();
                var inner = Sequence(tk => tk.Kind == K.Char && tk.Text == "}", integrand);
                if (IsChar("}")) Next();
                return inner;
            }
            var seq = new Seq();
            if (Peek.Kind != K.End) Item(seq, integrand);
            return seq;
        }

        private static string Paren(string s) => "(" + s + ")";

        /// <summary>A function argument without parentheses: a run of factors up to the next sign or function.</summary>
        private string Term(bool stopAtFunctions)
        {
            var seq = new Seq();
            while (Peek.Kind != K.End)
            {
                var t = Peek;
                if (t.Kind == K.Char && t.Text is "+" or "-" or "=" or "<" or ">" or "," or ";" or ")" or "]" or "}" or "&" or "|") break;
                if (t.Kind == K.Cmd && (t.Text is "\\" or "right" or "Rightarrow" or "implies" or "le" or "ge" or "leq" or "geq" or "ne" or "neq" or "pm" or "mp" or "cdot" or "times" or "div" or "quad" or "qquad" or "int" or "lim" or "sum" or "prod" or "to" or "rightarrow"
                    || stopAtFunctions && FunctionCommands.ContainsKey(t.Text)))
                {
                    if (t.Text is "cdot" or "times" && !stopAtFunctions) { Next(); Operator(seq, "*"); continue; }
                    break;
                }
                int save = _p;
                if (Differential() is not null) { _p = save; break; }
                Item(seq, false);
            }
            return seq.Text;
        }

        private void FunctionApplication(Seq seq, string name)
        {
            // Powers on the function name: sin^2 x = sin(x)^2, sin^{-1} x = asin(x); log_2 x = log(x, 2).
            string? power = null, baseArg = null;
            while (IsChar("^") || IsChar("_"))
            {
                bool sup = Next().Text == "^";
                var arg = ScriptArgument();
                if (sup) power = arg; else baseArg = arg;
            }
            if (power is "-1" or "(-1)" && name is "sin" or "cos" or "tan" or "sinh" or "cosh" or "tanh")
            {
                name = "a" + name;
                power = null;
            }
            string argument;
            if (IsChar("(") || IsCmd("left"))
            {
                var s = new Seq();
                Item(s, false);
                argument = StripParens(s.Text);
                // Powers written after the parenthesis: sin(x)^2.
                var tail = new Seq();
                PostfixAndOperand(tail, "");
                if (tail.Items.Count > 0 && tail.Items[0].Text.Length > 0) power ??= tail.Items[0].Text.TrimStart('^').Trim('(', ')');
            }
            else argument = Term(stopAtFunctions: true);
            var call = baseArg is not null && name == "log" ? $"log({argument}, {baseArg})" : $"{name}({argument})";
            if (power is not null) call = call + "^(" + power + ")";
            PostfixAndOperand(seq, call);
        }

        private static string StripParens(string s) =>
            s.Length >= 2 && s[0] == '(' && Matching(s, 0) == s.Length - 1 ? s[1..^1] : s;

        private void Command(string name, Seq seq, bool integrand)
        {
            switch (name)
            {
                case "frac": case "dfrac": case "tfrac": case "cfrac":
                    Fraction(seq, integrand);
                    return;
                case "sqrt":
                    {
                        string? index = null;
                        if (IsChar("["))
                        {
                            Next();
                            var idx = Sequence(tk => tk.Kind == K.Char && tk.Text == "]");
                            if (IsChar("]")) Next();
                            index = idx.Text;
                        }
                        var arg = Group();
                        PostfixAndOperand(seq, index is null ? $"sqrt({arg.Text})" : $"root({arg.Text}, {index})");
                        return;
                    }
                case "binom": case "dbinom": case "tbinom":
                    {
                        var n = Group(); var k = Group();
                        PostfixAndOperand(seq, $"binomial({n.Text}, {k.Text})");
                        return;
                    }
                case "left":
                    {
                        var open = DelimiterText();
                        var inner = Sequence(tk => tk.Kind == K.Cmd && tk.Text == "right" || tk.Kind == K.Cmd && tk.Text == "\\" && open == "{");
                        if (open == "{" && IsCmd("\\"))
                        {
                            // \left\{ a \\ b \right. : a system written with a brace.
                            seq.ExtraLines.Add(inner.Text);
                            while (IsCmd("\\"))
                            {
                                Next();
                                var row = Sequence(tk => tk.Kind == K.Cmd && tk.Text is "right" or "\\");
                                seq.ExtraLines.Add(row.Text);
                            }
                            if (IsCmd("right")) { Next(); SkipDelimiter(); }
                            MoveLastExtraToItems(seq);
                            return;
                        }
                        if (IsCmd("right")) Next();
                        var close = DelimiterText();
                        if (inner.ExtraLines.Count > 0 && open is "{" or ".")
                        {
                            // \left\{ \begin{matrix} rows \end{matrix} \right.
                            seq.ExtraLines.AddRange(inner.ExtraLines);
                            if (inner.Items.Count > 0) seq.ExtraLines.Add(inner.Text);
                            MoveLastExtraToItems(seq);
                            return;
                        }
                        string text = (open, close) switch
                        {
                            ("|", "|") => "abs(" + inner.Text + ")",
                            ("lfloor", _) => "floor(" + inner.Text + ")",
                            ("lceil", _) => "ceil(" + inner.Text + ")",
                            _ => "(" + inner.Text + ")",
                        };
                        if (inner.Differential is not null) seq.Differential = inner.Differential;
                        PostfixAndOperand(seq, text);
                        return;
                    }
                case "right":
                    SkipDelimiter();
                    return;
                case "lfloor": case "lceil":
                    {
                        var inner = Sequence(tk => tk.Kind == K.Cmd && tk.Text is "rfloor" or "rceil");
                        if (Peek.Kind == K.Cmd) Next();
                        PostfixAndOperand(seq, (name == "lfloor" ? "floor(" : "ceil(") + inner.Text + ")");
                        return;
                    }
                case "{":
                    {
                        var inner = Sequence(tk => tk.Kind == K.Cmd && tk.Text == "}");
                        if (IsCmd("}")) Next();
                        PostfixAndOperand(seq, "(" + inner.Text + ")");
                        return;
                    }
                case "int": case "intop":
                    Integral(seq);
                    return;
                case "lim": case "limits":
                    if (name == "limits") return;
                    Limit(seq);
                    return;
                case "sum": case "prod":
                    SumOrProduct(seq, name == "sum" ? "sum" : "product");
                    return;
                case "begin":
                    Environment(seq);
                    return;
                case "end":
                    Group();
                    return;
                case "text": case "mathrm": case "operatorname": case "mathit": case "mathbf": case "boldsymbol": case "mathsf": case "textrm": case "textit": case "mbox":
                    {
                        int save = _p;
                        var raw = RawGroup();
                        var word = raw.Trim();
                        if (FunctionCommands.TryGetValue(word, out var fn)) { FunctionApplication(seq, fn); return; }
                        if (word is "or" or "o" or "ó") { Operator(seq, " or "); return; }
                        if (word is "and" or "y") { Operator(seq, " and "); return; }
                        if (word.Length > 0 && word.All(char.IsLetterOrDigit) && word.Length <= 2)
                        {
                            _p = save;
                            var g = Group(integrand);
                            if (g.Differential is not null) seq.Differential = g.Differential;
                            if (g.Items.Count > 0) PostfixAndOperand(seq, g.Items.Count == 1 ? g.Items[0].Text : "(" + g.Text + ")");
                            return;
                        }
                        return; // prose
                    }
                case "cdot": case "times": case "ast":
                    Operator(seq, "*");
                    return;
                case "div":
                    Operator(seq, "/");
                    return;
                case "pm":
                    Operator(seq, "±");
                    return;
                case "mp":
                    Operator(seq, "∓");
                    return;
                case "le": case "leq": case "leqslant": Operator(seq, " <= "); return;
                case "ge": case "geq": case "geqslant": Operator(seq, " >= "); return;
                case "lt": Operator(seq, " < "); return;
                case "gt": Operator(seq, " > "); return;
                case "ne": case "neq": Operator(seq, " != "); return;
                case "approx": case "equiv": case "simeq": case "cong": Operator(seq, " = "); return;
                case "vee": case "lor": Operator(seq, " or "); return;
                case "wedge": case "land": Operator(seq, " and "); return;
                case "quad": case "qquad":
                    Operator(seq, " , ");
                    return;
                case "infty": PostfixAndOperand(seq, "oo"); return;
                case "pi": PostfixAndOperand(seq, "pi"); return;
                case "partial": PostfixAndOperand(seq, "d"); return;
                case "prime":
                    if (seq.Items.Count > 0 && seq.Items[^1].Operand) seq.Items[^1] = (true, seq.Items[^1].Text + "'");
                    return;
                case "circ":
                    if (seq.Items.Count > 0 && seq.Items[^1].Operand) seq.Items[^1] = (true, "(" + seq.Items[^1].Text + "*pi/180)");
                    return;
                case "|":
                    {
                        var inner = Sequence(tk => tk.Kind == K.Cmd && tk.Text == "|");
                        if (IsCmd("|")) Next();
                        PostfixAndOperand(seq, "abs(" + inner.Text + ")");
                        return;
                    }
                case "%": case ",": case ";": case ":": case "!": case " ": case "displaystyle": case "textstyle": case "ldots": case "cdots": case "dots":
                case "nonumber": case "notag": case "big": case "Big": case "bigg": case "Bigg": case "bigl": case "bigr": case "Bigl": case "Bigr": case "hline":
                case "qedhere": case "mathstrut": case "label": case "tag":
                    if (name is "label" or "tag") RawGroup();
                    return;
                case "\\":
                    // A line break inside a group: treat as a separator between alternatives.
                    Operator(seq, " , ");
                    return;
                case "to": case "rightarrow": case "Rightarrow": case "implies": case "therefore":
                    Operator(seq, " ⇒ ");
                    return;
            }
            if (FunctionCommands.TryGetValue(name, out var f)) { FunctionApplication(seq, f); return; }
            if (Greek.Contains(name)) { PostfixAndOperand(seq, Subscripted(name.StartsWith("var") ? name[3..] : name)); return; }
            // Unknown command: keep its name as a symbol.
            PostfixAndOperand(seq, name);
        }

        private static void MoveLastExtraToItems(Seq seq)
        {
            if (seq.ExtraLines.Count == 0) return;
            var last = seq.ExtraLines[^1];
            seq.ExtraLines.RemoveAt(seq.ExtraLines.Count - 1);
            seq.Items.Add((true, last));
        }

        private string DelimiterText()
        {
            var t = Next();
            return t.Kind == K.Char ? t.Text : t.Text switch { "{" => "{", "}" => "}", "|" => "|", "vert" => "|", "lvert" => "|", "rvert" => "|", "langle" or "rangle" => "(", _ => t.Text };
        }

        private void SkipDelimiter()
        {
            if (Peek.Kind != K.End) Next();
        }

        private string RawGroup()
        {
            if (!IsChar("{")) return Peek.Kind == K.Char ? Next().Text : "";
            Next();
            var sb = new StringBuilder();
            int depth = 1;
            while (Peek.Kind != K.End)
            {
                var t = Next();
                if (t.Kind == K.Char && t.Text == "{") depth++;
                if (t.Kind == K.Char && t.Text == "}" && --depth == 0) break;
                sb.Append(t.Kind == K.Cmd ? "\\" + t.Text : t.Text);
            }
            return sb.ToString();
        }

        // ---- structures ------------------------------------------------------------------------------

        private void Fraction(Seq seq, bool integrand)
        {
            int save = _p;
            // Derivative operators: d/dx, d²/dx², dy/dx, d²y/dx².
            var num = RawGroup().Replace(" ", "").Replace("\\mathrm{d}", "d").Replace("\\partial", "d").Replace("\\,", "");
            var den = RawGroup().Replace(" ", "").Replace("\\mathrm{d}", "d").Replace("\\partial", "d").Replace("\\,", "");
            var derivative = DerivativeOperator(num, den);
            if (derivative is { } d)
            {
                if (d.Function is not null)
                {
                    PostfixAndOperand(seq, d.Function + new string('\'', d.Order));
                    return;
                }
                string body;
                if (IsChar("(") || IsCmd("left") || IsChar("{")) { var s = new Seq(); Item(s, false); body = StripParens(s.Text); }
                else body = Term(stopAtFunctions: false);
                PostfixAndOperand(seq, d.Order == 1 ? $"diff({body}, {d.Variable})" : $"diff({body}, {d.Variable}, {d.Order})");
                return;
            }
            _p = save;
            var n = Group(integrand);
            var m = Group(integrand);
            if (n.Differential is not null || m.Differential is not null) seq.Differential = n.Differential ?? m.Differential;
            var nt = n.Items.Count == 0 ? "1" : n.Text;
            var mt = m.Items.Count == 0 ? "1" : m.Text;
            PostfixAndOperand(seq, $"(({nt})/({mt}))");
        }

        private sealed record Deriv(int Order, string Variable, string? Function);

        private static Deriv? DerivativeOperator(string num, string den)
        {
            // den: d x  or  d x ^ { n } / dx^n
            if (den.Length < 2 || den[0] != 'd' || !char.IsLetter(den[1])) return null;
            var variable = den[1].ToString();
            int order = 1;
            var rest = den[2..];
            if (rest.Length > 0)
            {
                var o = rest.TrimStart('^').Trim('{', '}');
                if (!int.TryParse(o, out order)) return null;
            }
            if (num.Length == 0 || num[0] != 'd') return null;
            var after = num[1..];
            int numOrder = 1;
            if (after.StartsWith('^'))
            {
                var m = after[1..];
                string digits;
                if (m.StartsWith('{')) { int close = m.IndexOf('}'); if (close < 0) return null; digits = m[1..close]; after = m[(close + 1)..]; }
                else { digits = m[..1]; after = m[1..]; }
                if (!int.TryParse(digits, out numOrder)) return null;
            }
            if (numOrder != order) return null;
            if (after.Length == 0) return new Deriv(order, variable, null);
            if (after.Length == 1 && char.IsLetter(after[0])) return new Deriv(order, variable, after);
            return null;
        }

        private void Integral(Seq seq)
        {
            string? lo = null, hi = null;
            while (IsChar("_") || IsChar("^") || IsCmd("limits") || IsCmd("nolimits"))
            {
                if (Peek.Kind == K.Cmd) { Next(); continue; }
                bool sub = Next().Text == "_";
                var arg = ScriptArgument();
                if (sub) lo = arg; else hi = arg;
            }
            var body = Sequence(tk => tk.Kind == K.Char && tk.Text is "=" or "}" or "&" or "<" or ">" || tk.Kind == K.Cmd && tk.Text is "\\" or "right" or "Rightarrow" or "le" or "ge" or "leq" or "geq",
                integrand: true);
            var variable = body.Differential ?? "x";
            var integrandText = body.Items.Count == 0 ? "1" : body.Text;
            var text = lo is not null && hi is not null
                ? $"integrate({integrandText}, {variable}, {lo}, {hi})"
                : $"integrate({integrandText}, {variable})";
            Operand(seq, text);
        }

        private void Limit(Seq seq)
        {
            string variable = "x", point = "0", direction = "";
            if (IsChar("_"))
            {
                Next();
                var raw = new Seq();
                if (IsChar("{"))
                {
                    Next();
                    raw = Sequence(tk => tk.Kind == K.Char && tk.Text == "}");
                    if (IsChar("}")) Next();
                }
                var text = raw.Text;
                int arrow = text.IndexOf('⇒');
                if (arrow > 0)
                {
                    variable = text[..arrow].Trim();
                    point = text[(arrow + 1)..].Trim();
                    // One-sided: 0^+ or 0^-
                    if (point.EndsWith("^(+)")) { direction = "+"; point = point[..^4]; }
                    else if (point.EndsWith("^(-)")) { direction = "-"; point = point[..^4]; }
                    else if (point.EndsWith("^+") || point.EndsWith("+")) { direction = "+"; point = point.TrimEnd('+', '^'); }
                    else if (point.EndsWith("^-")) { direction = "-"; point = point[..^2]; }
                }
            }
            string body;
            if (IsChar("(") || IsCmd("left")) { var s = new Seq(); Item(s, false); body = s.Text; }
            else body = Term(stopAtFunctions: false);
            // One-sided limits use the names right/left (no quotes, so the text can sit inside a string).
            Operand(seq, direction.Length > 0 ? $"limit({body}, {variable}, {point}, {(direction == "+" ? "right" : "left")})" : $"limit({body}, {variable}, {point})");
        }

        private void SumOrProduct(Seq seq, string fn)
        {
            string? lo = null, hi = null;
            while (IsChar("_") || IsChar("^") || IsCmd("limits"))
            {
                if (Peek.Kind == K.Cmd) { Next(); continue; }
                bool sub = Next().Text == "_";
                var arg = ScriptArgument();
                if (sub) lo = arg; else hi = arg;
            }
            string body;
            if (IsChar("(") || IsCmd("left")) { var s = new Seq(); Item(s, false); body = s.Text; }
            else body = Term(stopAtFunctions: false);
            string index = "k", start = "1";
            if (lo is not null && lo.Contains(" = "))
            {
                var parts = lo.Split(" = ", 2);
                index = parts[0].Trim(); start = parts[1].Trim();
            }
            Operand(seq, $"{fn}({body}, {index}, {start}, {hi ?? "oo"})");
        }

        private void Environment(Seq seq)
        {
            var env = RawGroup().Trim().TrimEnd('*');
            if (env == "array") RawGroup(); // column spec
            bool matrix = env is "pmatrix" or "bmatrix" or "Bmatrix" or "vmatrix" or "Vmatrix";
            var rows = new List<List<string>>();
            var row = new List<string>();
            while (Peek.Kind != K.End)
            {
                var cell = Sequence(tk => tk.Kind == K.Char && tk.Text == "&" || tk.Kind == K.Cmd && tk.Text is "\\" or "end" or "cr");
                row.Add(cell.Text);
                if (IsChar("&")) { Next(); continue; }
                if (IsCmd("\\") || IsCmd("cr")) { Next(); rows.Add(row); row = []; continue; }
                if (IsCmd("end")) { Next(); RawGroup(); break; }
                break;
            }
            if (row.Any(c => c.Trim().Length > 0)) rows.Add(row);
            rows = rows.Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
            if (matrix)
            {
                var m = "[" + string.Join(", ", rows.Select(r => "[" + string.Join(", ", r) + "]")) + "]";
                PostfixAndOperand(seq, env is "vmatrix" or "Vmatrix" ? $"det({m})" : m);
                return;
            }
            // Stacked lines (aligned, cases, matrix used for layout): one line per row; '&' only aligns.
            var lines = rows.Select(r => string.Concat(r).Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return;
            seq.ExtraLines.AddRange(lines.Take(lines.Count - 1));
            seq.Items.Add((true, lines[^1]));
        }
    }
}
