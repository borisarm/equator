using System.Globalization;
using System.Numerics;
using System.Text;
using Equator.Core.Algebra;
using Equator.Core.Engine;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Checking;

public enum Verdict { Given, Correct, Warning, Incorrect, Unchecked }

/// <summary>The verdict on one line of a worked solution.</summary>
public sealed record StepResult(int Index, string Input, Verdict Verdict, string Message);

/// <summary>The verdicts on all lines plus an overall verdict and summary.</summary>
public sealed record WorkReport(IReadOnlyList<StepResult> Steps, Verdict Overall, string Summary)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var s in Steps)
        {
            var mark = s.Verdict switch { Verdict.Given => "•", Verdict.Correct => "✓", Verdict.Warning => "!", Verdict.Incorrect => "✗", _ => "?" };
            sb.Append(mark).Append(' ').Append(s.Index + 1).Append(". ").Append(s.Input);
            if (s.Message.Length > 0) sb.Append("   — ").Append(s.Message);
            sb.AppendLine();
        }
        sb.Append(Summary);
        return sb.ToString();
    }
}

/// <summary>
/// Checks a worked solution written by a person, line by line, with the CAS. The first line states the task:
/// an equation, system or inequality to solve (each following line must have the same solution set), or an
/// expression to transform (each following line must be equal to it; for indefinite integrals up to a
/// constant). Steps after a mistake are checked against the previous line, and the final answer against
/// the original task.
/// </summary>
public static class WorkChecker
{
    private enum Mode { Transform, Solve, Inequality }

    private sealed record Piece(List<string> Parts, List<string> Relations);

    private sealed class Statement
    {
        public required string Text { get; init; }
        public required List<Piece> Pieces { get; init; }
        public bool Disjunction { get; init; }
        public bool Continuation { get; init; }
        public bool Empty { get; init; }
        public bool IsSinglePart => Pieces.Count == 1 && Pieces[0].Parts.Count == 1;
        public bool IsEquation => Pieces.Count == 1 && Pieces[0].Parts.Count == 2 && Pieces[0].Relations[0] == "=";
        public bool IsInequality => Pieces.Any(p => p.Relations.Any(r => r != "="));
    }

    public static WorkReport Check(IReadOnlyList<string> lines)
    {
        if (RowReduction.TryCheck(lines) is { } rowReduction) return rowReduction;
        var kernel = new Kernel();
        var steps = new List<StepResult>();
        var statements = new List<(int Index, Statement S)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0) continue;
            try { statements.Add((i, Parse(text))); }
            catch (MathException ex) { steps.Add(new StepResult(i, text, Verdict.Unchecked, Messages.T("Could not read this line: {0}", ex.Message))); }
        }
        if (statements.Count == 0) return Finish(steps, Messages.T("Nothing to check."));

        var first = statements[0];
        Mode mode;
        try { mode = DecideMode(kernel, first.S); }
        catch (MathException ex)
        {
            steps.Add(new StepResult(first.Index, first.S.Text, Verdict.Unchecked, Messages.T("Could not understand the task: {0}", ex.Message)));
            return Finish(steps, Messages.T("The first line could not be read."));
        }

        return mode switch
        {
            Mode.Transform => CheckTransform(kernel, statements, steps),
            Mode.Solve => CheckSolve(kernel, statements, steps),
            _ => CheckInequality(kernel, statements, steps),
        };
    }

    // ---- reading lines ------------------------------------------------------------------------------

    // English words only: single letters such as "y" or "o" would collide with variable names.
    private static readonly string[] OrWords = [" or ", " OR ", "∨"];
    private static readonly string[] AndWords = [" and ", " AND ", "∧"];

    private static Statement Parse(string text)
    {
        var t = text.Trim();
        foreach (var arrow in new[] { "⇒", "=>", "→", "∴", "therefore", "so " })
            if (t.StartsWith(arrow, StringComparison.OrdinalIgnoreCase)) t = t[arrow.Length..].Trim();
        var lower = t.ToLowerInvariant();
        if (lower is "no solution" or "no solutions" or "∅" or "emptyset" or "{}" or "no real solutions" or "sin solucion" or "sin solución")
            return new Statement { Text = text, Pieces = [], Empty = true };

        bool continuation = t.StartsWith('=') && !t.StartsWith("==");
        if (continuation) t = t[1..].Trim();

        bool disjunction = false;
        var alternatives = SplitTopLevel(t, OrWords);
        if (alternatives.Count > 1) disjunction = true;
        else
        {
            alternatives = SplitTopLevel(t, AndWords);
            if (alternatives.Count == 1)
            {
                alternatives = SplitTopLevel(t, [",", ";"]);
                // A comma list of single-variable values is a list of alternatives (x = 2, x = 3).
                disjunction = alternatives.Count > 1 && alternatives.All(a => a.Contains('=')) && SameLeftSide(alternatives);
            }
        }
        // ± and ∓ expand to two alternatives.
        var expanded = new List<string>();
        foreach (var a in alternatives)
        {
            if (a.Contains('±') || a.Contains('∓'))
            {
                expanded.Add(a.Replace('±', '+').Replace('∓', '-').Replace("=+", "=").Replace("= +", "= "));
                expanded.Add(a.Replace('±', '-').Replace('∓', '+'));
                disjunction = true;
            }
            else expanded.Add(a);
        }
        var pieces = expanded.Select(SplitRelations).ToList();
        if (pieces.Any(p => p.Parts.Any(part => part.Length == 0)))
            throw new MathException(Messages.T("a side of a relation is missing"));
        return new Statement { Text = text, Pieces = pieces, Disjunction = disjunction, Continuation = continuation };
    }

    private static bool SameLeftSide(List<string> items)
    {
        var lefts = items.Select(i => i.Split('=')[0].Trim()).Distinct().ToList();
        return lefts.Count == 1;
    }

    /// <summary>Splits at separators that are outside brackets.</summary>
    private static List<string> SplitTopLevel(string s, string[] separators)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0)
            {
                foreach (var sep in separators)
                {
                    if (string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0)
                    {
                        parts.Add(s[start..i]);
                        start = i + sep.Length;
                        i = start - 1;
                        break;
                    }
                }
            }
        }
        parts.Add(s[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>a = b = c, a &lt; b ≤ c: the parts and the relations between them (top level only).</summary>
    private static Piece SplitRelations(string s)
    {
        var parts = new List<string>();
        var rels = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}') { depth--; continue; }
            if (depth != 0) continue;
            bool nextIsEq = i + 1 < s.Length && s[i + 1] == '=';
            (string Rel, int Length)? found = c switch
            {
                '≤' => ("<=", 1),
                '≥' => (">=", 1),
                '≠' => ("!=", 1),
                '<' or '>' => nextIsEq ? (c + "=", 2) : (c.ToString(), 1),
                '!' when nextIsEq => ("!=", 2),
                '=' when !nextIsEq && !(i > 0 && s[i - 1] is ':' or '=') => ("=", 1),
                _ => null,
            };
            if (found is not { } f) continue;
            parts.Add(s[start..i].Trim());
            rels.Add(f.Rel);
            start = i + f.Length;
            i = start - 1;
        }
        parts.Add(s[start..].Trim());
        return new Piece(parts, rels);
    }

    private static Expr Eval(Kernel k, string text)
    {
        if (text.Length == 0) throw new MathException(Messages.T("empty expression"));
        return k.EvalText(text);
    }

    private static Mode DecideMode(Kernel k, Statement s)
    {
        if (s.IsInequality) return Mode.Inequality;
        if (s.Pieces.Count > 1) return Mode.Solve;
        var piece = s.Pieces[0];
        if (piece.Parts.Count == 1) return Mode.Transform;
        if (piece.Parts.Count == 2 && !s.Continuation)
        {
            // An identity (both sides always equal) is a transformation; otherwise an equation to solve.
            var a = Eval(k, piece.Parts[0]);
            var b = Eval(k, piece.Parts[1]);
            return Equivalent(a, b, null).Equivalent ? Mode.Transform : Mode.Solve;
        }
        return Mode.Transform;
    }

    // ---- transformations -------------------------------------------------------------------------------

    private static WorkReport CheckTransform(Kernel k, List<(int Index, Statement S)> statements, List<StepResult> steps)
    {
        Expr? current = null, original = null;
        string? integrationVariable = null;
        bool anyWrong = false;
        foreach (var (index, s) in statements)
        {
            bool firstLine = index == statements[0].Index;
            if (s.Pieces.Count != 1 || s.Empty)
            {
                steps.Add(new StepResult(index, s.Text, Verdict.Unchecked, Messages.T("Expected an expression here.")));
                continue;
            }
            var parts = s.Pieces[0].Parts;
            if (s.Pieces[0].Relations.Any(r => r != "="))
            {
                steps.Add(new StepResult(index, s.Text, Verdict.Unchecked, Messages.T("An inequality inside a calculation cannot be checked.")));
                continue;
            }
            var verdict = firstLine && parts.Count == 1 ? Verdict.Given : Verdict.Correct;
            var messages = new List<string>();
            foreach (var part in parts)
            {
                integrationVariable ??= IndefiniteIntegralVariable(part);
                Expr value;
                try { value = Eval(k, part); }
                catch (MathException ex) { verdict = Worst(verdict, Verdict.Unchecked); messages.Add(Messages.T("could not read '{0}': {1}", part, ex.Message)); continue; }
                if (current is null)
                {
                    current = original = value;
                    continue;
                }
                var eq = Equivalent(current, value, integrationVariable);
                if (eq.Equivalent)
                {
                    if (eq.UpToConstant) messages.Add(Messages.T("equal up to a constant of integration"));
                }
                else
                {
                    verdict = Worst(verdict, Verdict.Incorrect);
                    anyWrong = true;
                    messages.Add(Messages.T("'{0}' is not equal to the previous expression", part) + eq.Counterexample);
                }
                current = value;
            }
            if (index != statements[0].Index && NewSymbolsNote(k, s, statements[0].S) is { } note1) messages.Add(note1);
            steps.Add(new StepResult(index, s.Text, verdict, string.Join("; ", messages)));
        }
        if (original is null || current is null) return Finish(steps, Messages.T("The work could not be checked."));
        var final = Equivalent(original, current, integrationVariable);
        string summary;
        if (final.Equivalent && !anyWrong) summary = Messages.T("All steps are correct.") + (final.UpToConstant ? " " + Messages.T("(The result differs from the integral only by a constant, which is fine.)") : "");
        else if (final.Equivalent) summary = Messages.T("The final result is right, but some steps are wrong (the mistakes cancel out).");
        else summary = Messages.T("The final result is not correct: the expected value is {0}.", TextFormatter.Format(Solver.Tidy(original)));
        return Finish(steps, summary, finalWrong: !final.Equivalent);
    }

    /// <summary>The variable of an indefinite integral written in the line (integrate(f, x)), from the unevaluated parse.</summary>
    private static string? IndefiniteIntegralVariable(string text)
    {
        try
        {
            foreach (var st in Parsing.Parser.ParseProgram(text))
                foreach (var node in st.Tree.Subexpressions())
                    if (node is FnExpr { Name: "@call" } call && call.Args.Count == 3 && call.Args[0] is Sym { Name: "integrate" or "int" or "integral" } && call.Args[2] is Sym v)
                        return v.Name;
        }
        catch (Parsing.ParseException) { }
        return null;
    }

    private static int Rank(Verdict v) => v switch { Verdict.Given => 0, Verdict.Correct => 1, Verdict.Warning => 2, Verdict.Unchecked => 3, _ => 4 };
    private static Verdict Worst(Verdict a, Verdict b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>
    /// A hint when a line uses letters that do not occur in the task: usually a misread or miswritten symbol
    /// (a 9 read as a), occasionally a deliberate substitution.
    /// </summary>
    private static string? NewSymbolsNote(Kernel k, Statement line, Statement task)
    {
        static HashSet<string> Symbols(Kernel k, Statement st)
        {
            var set = new HashSet<string>();
            foreach (var p in st.Pieces)
                foreach (var part in p.Parts)
                {
                    try { foreach (var sym in Eval(k, part).FreeSymbols()) set.Add(sym.Name); }
                    catch (MathException) { }
                }
            return set;
        }
        var extra = Symbols(k, line).Except(Symbols(k, task)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (extra.Count == 0) return null;
        var names = string.Join(", ", extra);
        return Messages.T("note: {0} does not appear in the task; if a symbol was misread, edit this line", names);
    }

    private sealed record Equivalence(bool Equivalent, bool UpToConstant, string Counterexample);

    /// <summary>
    /// Whether two expressions are equal: symbolically if possible, otherwise numerically at several real
    /// sample points. With an integration variable, a constant difference is accepted.
    /// </summary>
    private static Equivalence Equivalent(Expr a, Expr b, string? integrationVariable)
    {
        // Antiderivatives the CAS could not find stay as integrate(...): compare derivatives instead.
        if (integrationVariable is not null)
        {
            var x = new Sym(integrationVariable);
            var da = Calculus.Derivative.Diff(a, x);
            var db = Calculus.Derivative.Diff(b, x);
            var d = NumericallyEqual(da, db);
            if (d.Equal)
            {
                var exact = NumericallyEqual(a, b);
                return new Equivalence(true, !exact.Equal, "");
            }
            return new Equivalence(false, false, d.Counterexample);
        }
        try
        {
            var diff = Simplifier.Simplify(a - b);
            if (diff.IsZero) return new Equivalence(true, false, "");
        }
        catch (MathException) { }
        var n = NumericallyEqual(a, b);
        return new Equivalence(n.Equal, false, n.Counterexample);
    }

    private static readonly double[] SampleValues = [0.37, 1.29, -0.83, 2.17, -1.61, 0.71, 3.07, -2.39];

    private static (bool Equal, string Counterexample) NumericallyEqual(Expr a, Expr b)
    {
        var syms = a.FreeSymbols().Union(b.FreeSymbols()).Select(s => s.Name).Distinct().ToList();
        int compared = 0;
        for (int trial = 0; trial < SampleValues.Length; trial++)
        {
            var vars = new Dictionary<string, Complex>();
            for (int j = 0; j < syms.Count; j++) vars[syms[j]] = SampleValues[(trial + 3 * j) % SampleValues.Length];
            Complex va, vb;
            try { va = NumericEval.Eval(a, vars); vb = NumericEval.Eval(b, vars); }
            catch (MathException) { continue; }
            if (!Finite(va) || !Finite(vb)) continue;
            compared++;
            if (Complex.Abs(va - vb) > 1e-8 * (1 + Complex.Abs(va) + Complex.Abs(vb)))
            {
                var values = string.Join(", ", syms.Select(s => $"{s} = {Fmt(vars[s].Real)}"));
                return (false, " " + (syms.Count == 0
                    ? Messages.T("({0} versus {1})", Fmt(va), Fmt(vb))
                    : Messages.T("({0} versus {1} at {2})", Fmt(va), Fmt(vb), values)));
            }
        }
        return (compared > 0, compared > 0 ? "" : " " + Messages.T("(the expressions could not be evaluated)"));
    }

    private static bool Finite(Complex c) => double.IsFinite(c.Real) && double.IsFinite(c.Imaginary);

    private static string Fmt(double v) => Math.Round(v, 6).ToString("G6", CultureInfo.InvariantCulture);
    private static string Fmt(Complex c) => Math.Abs(c.Imaginary) < 1e-12 ? Fmt(c.Real) : $"{Fmt(c.Real)} + {Fmt(c.Imaginary)}i";

    // ---- equations and systems ------------------------------------------------------------------------

    /// <summary>A solution set: a list of points (variable → numeric value) with display text.</summary>
    private sealed record SolutionSet(List<Dictionary<string, Complex>> Points, List<string> Display, bool Complete);

    private static WorkReport CheckSolve(Kernel k, List<(int Index, Statement S)> statements, List<StepResult> steps)
    {
        // Unknowns: the symbols of the task (prefer x, y, z, t when there are parameters too).
        var firstEqs = Equations(k, statements[0].S);
        var symbols = firstEqs.SelectMany(e => e.FreeSymbols()).Distinct().ToList();
        int needed = Math.Max(1, firstEqs.Count);
        var preferred = new[] { "x", "y", "z", "t", "u", "v", "w" };
        var unknowns = symbols.Count <= needed
            ? symbols
            : symbols.OrderBy(s => Array.IndexOf(preferred, s.Name) is var p && p >= 0 ? p : 100).ThenBy(s => s.Name, StringComparer.Ordinal).Take(needed).ToList();
        // Work over the reals, as in school mathematics.
        foreach (var u in unknowns) k.Assumptions = k.Assumptions.AddProperty(u, "real");

        SolutionSet? previous = null, original = null;
        bool anyWrong = false;
        foreach (var (index, s) in statements)
        {
            SolutionSet set;
            try { set = Solutions(k, s, unknowns); }
            catch (MathException ex)
            {
                steps.Add(new StepResult(index, s.Text, Verdict.Unchecked, Messages.T("Could not solve this line: {0}", ex.Message)));
                continue;
            }
            if (previous is null)
            {
                previous = original = set;
                var given = set.Complete ? Messages.T("solutions: {0}", Describe(set)) : Messages.T("(the solutions could only be estimated numerically)");
                steps.Add(new StepResult(index, s.Text, Verdict.Given, given));
                continue;
            }
            var (verdict, message) = Compare(previous, set, againstTask: false, original);
            // Chained right-hand sides (2x = 7 − 3 = 4) must also agree among themselves.
            var inner = InnerChainProblem(k, s);
            if (inner is not null) { verdict = Verdict.Incorrect; message = (message.Length > 0 ? message + "; " : "") + inner; }
            if (verdict == Verdict.Incorrect) anyWrong = true;
            if (NewSymbolsNote(k, s, statements[0].S) is { } note2) message = message.Length > 0 ? message + "; " + note2 : note2;
            steps.Add(new StepResult(index, s.Text, verdict, message));
            previous = set;
        }
        if (original is null || previous is null) return Finish(steps, Messages.T("The work could not be checked."));
        var (finalVerdict, finalMessage) = Compare(original, previous, againstTask: true, null);
        string summary;
        bool solved = IsExplicitSolution(statements[^1].S, unknowns);
        if (finalVerdict == Verdict.Correct)
            summary = (anyWrong ? Messages.T("The final answer is right, but some steps are wrong.") : Messages.T("All steps are correct.")) + " "
                + (!solved ? Messages.T("The last line is equivalent to the task but not yet solved explicitly.")
                    : original.Points.Count == 0 ? Messages.T("There is indeed no real solution.")
                    : original.Points.Count == 1 ? Messages.T("The solution {0} is right.", Describe(original))
                    : Messages.T("The solutions {0} are right.", Describe(original)));
        else
            summary = Messages.T("The final answer is not correct: {0}. The correct solutions are {1}.", finalMessage, Describe(original));
        return Finish(steps, summary, finalWrong: finalVerdict != Verdict.Correct);
    }

    private static bool IsExplicitSolution(Statement s, List<Sym> unknowns) =>
        s.Empty || s.Pieces.All(p => p.Parts.Count == 2 && unknowns.Any(u => u.Name == p.Parts[0].Trim()) && !unknowns.Any(u => ContainsSymbol(p.Parts[1], u.Name)));

    private static bool ContainsSymbol(string text, string name)
    {
        for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            bool before = i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_';
            int end = i + name.Length;
            bool after = end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
            if (before && after) return true;
        }
        return false;
    }

    private static List<Expr> Equations(Kernel k, Statement s)
    {
        var eqs = new List<Expr>();
        foreach (var p in s.Pieces)
        {
            if (p.Parts.Count < 2 || p.Relations.Any(r => r != "=")) throw new MathException(Messages.T("expected an equation"));
            eqs.Add(Expr.Equation(Eval(k, p.Parts[0]), Eval(k, p.Parts[^1])));
        }
        return eqs;
    }

    private static string? InnerChainProblem(Kernel k, Statement s)
    {
        foreach (var p in s.Pieces)
        {
            if (p.Parts.Count < 3) continue;
            for (int j = 1; j + 1 < p.Parts.Count; j++)
            {
                var a = Eval(k, p.Parts[j]);
                var b = Eval(k, p.Parts[j + 1]);
                var eq = Equivalent(a, b, null);
                if (!eq.Equivalent) return Messages.T("'{0}' is not equal to '{1}'", p.Parts[j], p.Parts[j + 1]) + eq.Counterexample;
            }
        }
        return null;
    }

    private static SolutionSet Solutions(Kernel k, Statement s, List<Sym> unknowns)
    {
        if (s.Empty) return new SolutionSet([], [], true);
        using var _ = Assumptions.Scope(k.Assumptions);
        var vars = Expr.List(unknowns);
        if (s.Disjunction)
        {
            var all = new SolutionSet([], [], true);
            foreach (var p in s.Pieces)
            {
                var one = Solutions(k, new Statement { Text = s.Text, Pieces = [p] }, unknowns);
                all = new SolutionSet([.. all.Points, .. one.Points], [.. all.Display, .. one.Display], all.Complete && one.Complete);
            }
            return Dedupe(all);
        }
        var eqs = Equations(k, s);
        if (eqs.Any(e => e is Sym { Name: "True" })) eqs = eqs.Where(e => e is not Sym).ToList();
        var result = Solver.Solve(eqs.Count == 1 ? eqs[0] : Expr.List(eqs), unknowns.Count == 1 ? unknowns[0] : vars);
        var points = new List<Dictionary<string, Complex>>();
        var display = new List<string>();
        bool complete = !result.ContainsAny(z => z is Real);
        foreach (var sol in ((ListExpr)result).Items)
        {
            var eqList = sol is ListExpr l ? l.Items : [sol];
            var point = new Dictionary<string, Complex>();
            foreach (var e in eqList.OfType<EqExpr>())
            {
                if (e.Lhs is not Sym v) continue;
                if (!e.Rhs.IsConstant) { complete = false; continue; }
                try { point[v.Name] = NumericEval.Eval(e.Rhs); }
                catch (MathException) { complete = false; }
            }
            if (point.Count == 0) continue;
            points.Add(point);
            display.Add(string.Join(", ", eqList.Select(TextFormatter.Format)));
        }
        return Dedupe(new SolutionSet(points, display, complete));
    }

    private static SolutionSet Dedupe(SolutionSet s)
    {
        var points = new List<Dictionary<string, Complex>>();
        var display = new List<string>();
        for (int i = 0; i < s.Points.Count; i++)
        {
            if (points.Any(p => Same(p, s.Points[i]))) continue;
            points.Add(s.Points[i]);
            display.Add(s.Display[i]);
        }
        return new SolutionSet(points, display, s.Complete);
    }

    private static bool Same(Dictionary<string, Complex> a, Dictionary<string, Complex> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Complex.Abs(v - kv.Value) < 1e-8 * (1 + Complex.Abs(v)));

    /// <param name="task">The original task's solutions: dropping values that never solved it (extraneous roots) is fine.</param>
    /// <param name="againstTask">Comparing with the original task (the final answer) rather than with the previous line.</param>
    private static (Verdict, string) Compare(SolutionSet before, SolutionSet after, bool againstTask, SolutionSet? task)
    {
        var dropped = before.Points.Select((p, i) => (p, i)).Where(t => !after.Points.Any(q => Same(t.p, q))).ToList();
        var discarded = task is null ? [] : dropped.Where(t => !task.Points.Any(q => Same(t.p, q))).Select(t => before.Display[t.i]).ToList();
        var lost = dropped.Select(t => before.Display[t.i]).Where(d => !discarded.Contains(d)).ToList();
        var gained = after.Points.Select((p, i) => (p, i)).Where(t => !before.Points.Any(q => Same(t.p, q))).Select(t => after.Display[t.i]).ToList();
        if (lost.Count == 0 && gained.Count == 0)
        {
            var note = discarded.Count > 0 ? Messages.T("correctly discards {0}, which does not solve the original task", string.Join("; ", discarded)) : "";
            if (!(before.Complete && after.Complete)) note = note.Length > 0 ? note + " " + Messages.T("(checked numerically)") : Messages.T("checked numerically");
            return (Verdict.Correct, note);
        }
        var parts = new List<string>();
        var lostText = string.Join("; ", lost);
        var gainedText = string.Join("; ", gained);
        if (lost.Count > 0) parts.Add((lost.Count, againstTask) switch
        {
            (1, false) => Messages.T("loses the solution {0} of the previous line", lostText),
            (1, true) => Messages.T("loses the solution {0} of the original task", lostText),
            (_, false) => Messages.T("loses the solutions {0} of the previous line", lostText),
            (_, true) => Messages.T("loses the solutions {0} of the original task", lostText),
        });
        if (gained.Count > 0) parts.Add((gained.Count, againstTask) switch
        {
            (1, false) => Messages.T("introduces {0}, which is not a solution of the previous line", gainedText),
            (1, true) => Messages.T("introduces {0}, which is not a solution of the original task", gainedText),
            (_, false) => Messages.T("introduces {0}, which are not solutions of the previous line", gainedText),
            (_, true) => Messages.T("introduces {0}, which are not solutions of the original task", gainedText),
        });
        // Extra roots alone (e.g. after squaring) are only a warning: they can still be rejected later.
        var verdict = lost.Count == 0 && !againstTask ? Verdict.Warning : Verdict.Incorrect;
        var message = string.Join(Messages.T(" and "), parts);
        if (verdict == Verdict.Warning) message += " " + Messages.T("(an extra root, e.g. from squaring; it must be discarded at the end)");
        return (verdict, message);
    }

    private static string Describe(SolutionSet s) => s.Points.Count == 0 ? Messages.T("none (no real solution)") : string.Join("; ", s.Display);

    // ---- inequalities -------------------------------------------------------------------------------

    private static WorkReport CheckInequality(Kernel k, List<(int Index, Statement S)> statements, List<StepResult> steps)
    {
        var symbols = statements[0].S.Pieces.SelectMany(p => p.Parts).SelectMany(t => Eval(k, t).FreeSymbols()).Distinct().ToList();
        if (symbols.Count != 1)
        {
            steps.Add(new StepResult(statements[0].Index, statements[0].S.Text, Verdict.Unchecked, Messages.T("Only inequalities in one variable can be checked.")));
            return Finish(steps, Messages.T("The work could not be checked."));
        }
        var x = symbols[0];
        Func<double, bool>? previous = null, original = null;
        bool anyWrong = false;
        foreach (var (index, s) in statements)
        {
            Func<double, bool> member;
            try { member = Membership(k, s, x); }
            catch (MathException ex)
            {
                steps.Add(new StepResult(index, s.Text, Verdict.Unchecked, Messages.T("Could not read this line: {0}", ex.Message)));
                continue;
            }
            if (previous is null)
            {
                previous = original = member;
                steps.Add(new StepResult(index, s.Text, Verdict.Given, ""));
                continue;
            }
            var diff = FindDifference(previous, member);
            if (diff is null) steps.Add(new StepResult(index, s.Text, Verdict.Correct, ""));
            else
            {
                anyWrong = true;
                steps.Add(new StepResult(index, s.Text, Verdict.Incorrect,
                    previous(diff.Value)
                        ? Messages.T("{0} satisfies the previous line but not this one", $"{x.Name} = {Fmt(diff.Value)}")
                        : Messages.T("{0} satisfies this line but not the previous one", $"{x.Name} = {Fmt(diff.Value)}")));
            }
            previous = member;
        }
        if (original is null || previous is null) return Finish(steps, Messages.T("The work could not be checked."));
        var final = FindDifference(original, previous);
        return Finish(steps, final is null
            ? anyWrong ? Messages.T("The final answer is right, but some steps are wrong.") : Messages.T("All steps are correct.")
            : Messages.T("The final answer is not correct: at {0} it disagrees with the original inequality.", $"{x.Name} = {Fmt(final.Value)}"),
            finalWrong: final is not null);
    }

    private static Func<double, bool> Membership(Kernel k, Statement s, Sym x)
    {
        var conditions = new List<Func<double, bool>>();
        foreach (var p in s.Pieces)
        {
            var values = p.Parts.Select(t => Eval(k, t)).ToList();
            var tests = new List<Func<double, bool>>();
            for (int j = 0; j < p.Relations.Count; j++)
            {
                var lhs = values[j]; var rhs = values[j + 1]; var op = p.Relations[j];
                tests.Add(v =>
                {
                    var vars = new Dictionary<string, Complex> { [x.Name] = v };
                    Complex a, b;
                    try { a = NumericEval.Eval(lhs, vars); b = NumericEval.Eval(rhs, vars); }
                    catch (MathException) { return false; }
                    if (Math.Abs(a.Imaginary) > 1e-12 || Math.Abs(b.Imaginary) > 1e-12 || !Finite(a) || !Finite(b)) return false;
                    double d = a.Real - b.Real;
                    return op switch { "<" => d < 0, ">" => d > 0, "<=" => d <= 1e-12, ">=" => d >= -1e-12, "!=" => Math.Abs(d) > 1e-12, _ => Math.Abs(d) <= 1e-12 };
                });
            }
            conditions.Add(v => tests.All(t => t(v)));
        }
        return s.Disjunction || s.Pieces.Count > 1 && s.Text.Contains(" or ", StringComparison.OrdinalIgnoreCase)
            ? v => conditions.Any(c => c(v))
            : v => conditions.All(c => c(v));
    }

    private static double? FindDifference(Func<double, bool> a, Func<double, bool> b)
    {
        for (double t = -20.05; t <= 20; t += 0.1)
            if (a(t) != b(t)) return Math.Round(t, 2);
        foreach (var t in new[] { -1000.5, -100.5, 100.5, 1000.5 })
            if (a(t) != b(t)) return t;
        return null;
    }

    internal static WorkReport Finish(List<StepResult> steps, string summary, bool finalWrong = false)
    {
        steps.Sort((a, b) => a.Index.CompareTo(b.Index));
        var overall = steps.Any(s => s.Verdict == Verdict.Incorrect) || finalWrong
            ? Verdict.Incorrect
            : steps.Any(s => s.Verdict == Verdict.Warning) ? Verdict.Warning
            : steps.All(s => s.Verdict is Verdict.Unchecked) ? Verdict.Unchecked
            : Verdict.Correct;
        return new WorkReport(steps, overall, summary);
    }
}
