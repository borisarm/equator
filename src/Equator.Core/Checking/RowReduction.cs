using System.Text;
using System.Text.RegularExpressions;
using Equator.Core.Algebra;
using Equator.Core.Engine;
using Equator.Core.Printing;

namespace Equator.Core.Checking;

/// <summary>
/// Checks Gaussian elimination done by hand: the first line is a matrix (an augmented matrix for a system), and
/// each following line a matrix obtained from the previous one by row operations. A line may name its
/// operations before a colon, "R2 → R2 - 3R1, R3 → R3 + R1: [[...]]"; they are then applied and compared entry by
/// entry. Without them the line must be row-equivalent to the previous one (same reduced row echelon form), and
/// the operations are recognised and described. Several operations on one line act on the previous matrix
/// (simultaneously), as in textbooks; row swaps are applied first.
/// </summary>
internal static partial class RowReduction
{
    private sealed record Line(int Index, string Text, string? Operations, Expr[,] Matrix);

    /// <summary>The report when the first line is a matrix, otherwise null (the task is something else).</summary>
    public static WorkReport? TryCheck(IReadOnlyList<string> lines)
    {
        var kernel = new Kernel();
        int first = -1;
        for (int i = 0; i < lines.Count && first < 0; i++) if (lines[i].Trim().Length > 0) first = i;
        if (first < 0) return null;
        var (firstOps, firstBody) = SplitLabel(lines[first].Trim());
        if (firstOps is not null || !TryMatrix(kernel, firstBody, out _)) return null;

        var steps = new List<StepResult>();
        var parsed = new List<Line>();
        for (int i = first; i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0) continue;
            var (ops, body) = SplitLabel(text);
            if (!TryMatrix(kernel, body, out var m))
            {
                steps.Add(new StepResult(i, text, Verdict.Unchecked, Messages.T("Expected a matrix here.")));
                continue;
            }
            parsed.Add(new Line(i, text, ops, m!));
        }

        var original = parsed[0].Matrix;
        steps.Add(new StepResult(parsed[0].Index, parsed[0].Text, Verdict.Given, ""));
        var previous = original;
        bool anyWrong = false;
        foreach (var line in parsed.Skip(1))
        {
            var (verdict, message) = CheckStep(kernel, previous, line);
            if (verdict == Verdict.Incorrect) anyWrong = true;
            steps.Add(new StepResult(line.Index, line.Text, verdict, message));
            // After a mistake, later steps are checked against what was written.
            if (SameSize(previous, line.Matrix)) previous = line.Matrix;
        }

        string summary;
        var last = parsed[^1].Matrix;
        bool equivalent = SameSize(original, last) && RowEquivalent(original, last);
        if (!anyWrong && equivalent)
            summary = IsEchelon(last, reduced: true) ? Messages.T("All steps are correct, and the last matrix is in reduced row echelon form.")
                : IsEchelon(last, reduced: false) ? Messages.T("All steps are correct; the last matrix is in row echelon form, but not yet reduced.")
                : Messages.T("All steps are correct, but the last matrix is not in row echelon form yet.");
        else if (equivalent)
            summary = Messages.T("The final result is right, but some steps are wrong (the mistakes cancel out).");
        else
            summary = Messages.T("The last matrix is not row-equivalent to the first one. The reduced row echelon form is {0}.",
                Format(LinearAlgebra.Rref(original).Matrix));
        return WorkChecker.Finish(steps, summary, finalWrong: !equivalent);
    }

    // ---- reading lines --------------------------------------------------------------------------------

    /// <summary>"ops: matrix" → (ops, matrix); a line without a top-level colon has no operations.</summary>
    private static (string? Ops, string Body) SplitLabel(string text)
    {
        var t = text;
        foreach (var prefix in new[] { "~", "∼", "⇒", "=>", "→", "->", "⟶" })
            if (t.StartsWith(prefix, StringComparison.Ordinal)) { t = t[prefix.Length..].TrimStart(); break; }
        int depth = 0;
        for (int i = t.Length - 1; i >= 0; i--)
        {
            char c = t[i];
            if (c is ']' or ')' or '}') depth++;
            else if (c is '[' or '(' or '{') depth--;
            else if (c == ':' && depth == 0 && (i + 1 >= t.Length || t[i + 1] != '='))
            {
                var ops = t[..i].Trim();
                return (ops.Length == 0 ? null : ops, t[(i + 1)..].Trim());
            }
        }
        return (null, t);
    }

    private static bool TryMatrix(Kernel k, string text, out Expr[,]? matrix)
    {
        matrix = null;
        if (!text.StartsWith('[') && !text.StartsWith('{')) return false;
        try
        {
            var value = k.EvalText(AugmentedBars(text));
            if (!LinearAlgebra.IsMatrix(value)) return false;
            matrix = LinearAlgebra.ToArray(value);
            return true;
        }
        catch (Exception ex) when (ex is MathException or Parsing.ParseException)
        {
            return false;
        }
    }

    /// <summary>An augmented matrix written [[1, 2 | 3], [4, 5 | 6]]: the bar separates entries like a comma.</summary>
    private static string AugmentedBars(string text) => text.Contains('|') && !text.Contains("||") ? text.Replace('|', ',') : text;

    // ---- one step -----------------------------------------------------------------------------------

    private static (Verdict, string) CheckStep(Kernel k, Expr[,] before, Line line)
    {
        var after = line.Matrix;
        if (!SameSize(before, after))
            return (Verdict.Incorrect, Messages.T("The matrix has a different size ({0}×{1} instead of {2}×{3}).",
                after.GetLength(0), after.GetLength(1), before.GetLength(0), before.GetLength(1)));
        string? unreadable = null;
        if (line.Operations is { } ops)
        {
            try { return CheckStatedOperations(before, after, ops, Transform(k, ops, before.GetLength(0))); }
            catch (MathException ex) { unreadable = ex.Message; }
        }
        var (verdict, message) = Equal(before, after) ? (Verdict.Correct, Messages.T("unchanged"))
            : RowEquivalent(before, after) ? (Verdict.Correct, Describe(before, after) ?? Messages.T("row-equivalent to the previous matrix"))
            : (Verdict.Incorrect, Diagnose(before, after));
        // An operation that cannot be read does not stop the matrix itself from being checked.
        return unreadable is null ? (verdict, message) : (verdict, unreadable + " " + message);
    }

    private static (Verdict, string) CheckStatedOperations(Expr[,] before, Expr[,] after, string ops, Expr[,] t)
    {
        int rows = before.GetLength(0);
        if (LinearAlgebra.Simp(LinearAlgebra.Determinant(LinearAlgebra.FromArray(t))).IsZero)
            return (Verdict.Incorrect, Messages.T("{0} is not a valid row operation: it cannot be undone (a row is multiplied by 0 or lost).", ops));
        var expected = Multiply(t, before);
        if (Equal(expected, after)) return (Verdict.Correct, "");
        var wrong = Enumerable.Range(0, rows).Where(i => !RowEqual(expected, after, i)).ToList();
        var messages = wrong.Select(i => Messages.T("row {0} should be {1}", i + 1, Format(Row(expected, i)))).ToList();
        if (RowEquivalent(before, after)) messages.Add(Messages.T("the matrix is still row-equivalent to the previous one"));
        return (Verdict.Incorrect, string.Join("; ", messages));
    }

    // ---- row operations written by the student --------------------------------------------------------

    [GeneratedRegex(@"(?<![A-Za-z_])[RrFfLl]\s?([0-9₀-₉]+)")]
    private static partial Regex RowToken();

    private static readonly string[] SwapArrows = ["↔", "<->", "<=>", "⇔", "⇄", "⟷"];
    private static readonly string[] AssignArrows = ["⟶", "→", "->", "←", "<-", ":=", "="];

    /// <summary>
    /// The matrix T with (new rows) = T·(old rows) for a list of operations "R2 → R2 - 3R1, R1 ↔ R3, 1/2 R2 → R2".
    /// Swaps are applied first; replacements all read the rows of the previous matrix.
    /// </summary>
    private static Expr[,] Transform(Kernel k, string ops, int rows)
    {
        var perm = Enumerable.Range(0, rows).ToArray();
        var replacements = new List<(int Target, Expr[] Coefficients, string Text)>();
        foreach (var raw in Regex.Split(ops, @"\s*(?:,|;|\band\b|\by\b|\bet\b)\s*"))
        {
            var op = raw.Trim();
            if (op.Length == 0) continue;
            if (SwapArrows.FirstOrDefault(op.Contains) is { } swap)
            {
                var sides = op.Split(swap);
                if (sides.Length != 2 || LoneRow(sides[0], rows) is not { } r1 || LoneRow(sides[1], rows) is not { } r2)
                    throw new MathException(Messages.T("Could not read the row operation '{0}'.", op));
                (perm[r1], perm[r2]) = (perm[r2], perm[r1]);
                continue;
            }
            var arrow = AssignArrows.FirstOrDefault(op.Contains) ?? throw new MathException(Messages.T("Could not read the row operation '{0}'.", op));
            int at = op.IndexOf(arrow, StringComparison.Ordinal);
            string left = op[..at], right = op[(at + arrow.Length)..];
            int target;
            string expression;
            // "R2 → R2 - 3R1" names the row first; "-3R1 + R2 → R2" (as in many textbooks) last.
            if (LoneRow(left, rows) is { } l) { target = l; expression = right; }
            else if (LoneRow(right, rows) is { } r) { target = r; expression = left; }
            else throw new MathException(Messages.T("Could not read the row operation '{0}'.", op));
            replacements.Add((target, Coefficients(k, expression, rows, op), op));
        }
        var t = new Expr[rows, rows];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < rows; j++)
                t[i, j] = perm[i] == j ? Expr.One : Expr.Zero;
        // Replacements read the rows after the swaps: compose with the permutation.
        var swapped = (Expr[,])t.Clone();
        foreach (var (target, coefficients, _) in replacements)
            for (int j = 0; j < rows; j++)
                t[target, j] = LinearAlgebra.Simp(Expr.Plus(Enumerable.Range(0, rows).Select(c => coefficients[c] * swapped[c, j])));
        return t;
    }

    private static int? LoneRow(string text, int rows)
    {
        var m = RowToken().Match(text.Trim());
        if (!m.Success || m.Index != 0 || m.Length != text.Trim().Length) return null;
        int r = RowNumber(m.Groups[1].Value);
        return r >= 1 && r <= rows ? r - 1 : null;
    }

    private static int RowNumber(string digits) =>
        int.Parse(new string(digits.Select(c => c is >= '₀' and <= '₉' ? (char)('0' + (c - '₀')) : c).ToArray()));

    /// <summary>The coefficients c with expression = Σ c_j R_j; the expression must be linear in the rows.</summary>
    private static Expr[] Coefficients(Kernel k, string expression, int rows, string op)
    {
        // Each row becomes a variable rowop__k, so the kernel can read coefficients such as (1/2), -3 or a.
        var text = RowToken().Replace(expression, m =>
        {
            int r = RowNumber(m.Groups[1].Value);
            if (r < 1 || r > rows) throw new MathException(Messages.T("Could not read the row operation '{0}'.", op));
            return "(rowop__" + r + ")";
        });
        Expr value;
        try { value = Expansion.Expand(k.EvalText(text)); }
        catch (Exception ex) when (ex is MathException or Parsing.ParseException)
        {
            throw new MathException(Messages.T("Could not read the row operation '{0}'.", op));
        }
        var rowSyms = Enumerable.Range(1, rows).Select(r => new Sym("rowop__" + r)).ToArray();
        var c = rowSyms.Select(s => LinearAlgebra.Simp(Calculus.Derivative.Diff(value, s))).ToArray();
        var rest = LinearAlgebra.Simp(Expansion.Expand(value - Expr.Plus(rowSyms.Select((s, i) => c[i] * s))));
        if (!rest.IsZero || c.Any(x => rowSyms.Any(x.Contains)))
            throw new MathException(Messages.T("Could not read the row operation '{0}'.", op));
        return c;
    }

    // ---- recognising and diagnosing steps ---------------------------------------------------------------

    /// <summary>Names the operations that turn before into after, when each changed row is a simple combination.</summary>
    private static string? Describe(Expr[,] before, Expr[,] after)
    {
        int rows = before.GetLength(0);
        var changed = Enumerable.Range(0, rows).Where(i => !RowEqual(before, after, i)).ToList();
        // A swap of two rows.
        if (changed.Count == 2 && RowsMatch(before, changed[0], after, changed[1]) && RowsMatch(before, changed[1], after, changed[0]))
            return Label(changed[0]) + " ↔ " + Label(changed[1]);
        var parts = new List<string>();
        foreach (var i in changed)
        {
            var c = Combination(before, Row(after, i));
            if (c is null || c[i].IsZero) return null;
            parts.Add(Label(i) + " → " + Combination(c, i));
        }
        return string.Join(", ", parts);
    }

    /// <summary>Why after is not row-equivalent to before: the rows that are not combinations of the previous rows, with a guess at the intended operation.</summary>
    private static string Diagnose(Expr[,] before, Expr[,] after)
    {
        int rows = before.GetLength(0);
        var messages = new List<string>();
        foreach (var i in Enumerable.Range(0, rows).Where(i => !RowEqual(before, after, i)))
        {
            var written = Row(after, i);
            if (Combination(before, written) is not null) continue;
            var message = Messages.T("row {0} is not a combination of the rows of the previous matrix", i + 1);
            if (Guess(before, i, written) is { } guess)
                message += "; " + Messages.T("{0} would give {1}", guess.Operation, Format(guess.Row));
            messages.Add(message);
        }
        if (messages.Count == 0)
        {
            int r0 = LinearAlgebra.Rref(before).Pivots.Count, r1 = LinearAlgebra.Rref(after).Pivots.Count;
            messages.Add(Messages.T("the rows no longer span the same space: the rank changed from {0} to {1}", r0, r1));
        }
        return string.Join("; ", messages);
    }

    /// <summary>
    /// The single operation (Ri → c·Ri or Ri → Ri + c·Rj) closest to the written row: the coefficient is taken
    /// from one column and the result must agree with the written row in all but at most half of the entries.
    /// </summary>
    private static (string Operation, Expr[] Row)? Guess(Expr[,] before, int i, Expr[] written)
    {
        int cols = before.GetLength(1), rows = before.GetLength(0);
        var old = Row(before, i);
        (string, Expr[], int)? best = null;
        void Consider(string op, Expr[] candidate)
        {
            int mismatches = Enumerable.Range(0, cols).Count(j => !IsZero(candidate[j] - written[j]));
            if (mismatches == 0 || 2 * mismatches > cols) return;
            if (best is null || mismatches < best.Value.Item3) best = (op, candidate, mismatches);
        }
        for (int col = 0; col < cols; col++)
        {
            if (!IsZero(old[col]))
            {
                var c = LinearAlgebra.Simp(written[col] / old[col]);
                if (!c.IsZero && !c.IsOne) Consider(Label(i) + " → " + Term(c, i, first: true), old.Select(x => LinearAlgebra.Simp(c * x)).ToArray());
            }
            for (int j = 0; j < rows; j++)
            {
                if (j == i || IsZero(before[j, col])) continue;
                var c = LinearAlgebra.Simp((written[col] - old[col]) / before[j, col]);
                if (c.IsZero) continue;
                Consider(Label(i) + " → " + Label(i) + Term(c, j, first: false),
                    Enumerable.Range(0, cols).Select(x => LinearAlgebra.Simp(old[x] + c * before[j, x])).ToArray());
            }
        }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    /// <summary>Coefficients c with Σ c_j·(row j of m) = v, or null when v is not in the row space.</summary>
    private static Expr[]? Combination(Expr[,] m, Expr[] v)
    {
        int rows = m.GetLength(0), cols = m.GetLength(1);
        var aug = new Expr[cols, rows + 1];
        for (int j = 0; j < cols; j++)
        {
            for (int i = 0; i < rows; i++) aug[j, i] = m[i, j];
            aug[j, rows] = v[j];
        }
        var (r, pivots) = LinearAlgebra.Rref(aug);
        if (pivots.Contains(rows)) return null;
        var c = Enumerable.Repeat<Expr>(Expr.Zero, rows).ToArray();
        for (int p = 0; p < pivots.Count; p++) c[pivots[p]] = r[p, rows];
        return c;
    }

    /// <summary>Σ c_j·R_j written with the target row first: "R2 - 3R1", "-(1/2)R2".</summary>
    private static string Combination(Expr[] c, int target)
    {
        var sb = new StringBuilder(Term(c[target], target, first: true));
        for (int j = 0; j < c.Length; j++)
            if (j != target && !c[j].IsZero) sb.Append(Term(c[j], j, first: false));
        return sb.ToString();
    }

    /// <summary>"R2", " - 3R1", " + (1/2)R3", "-R1" ...</summary>
    private static string Term(Expr c, int row, bool first)
    {
        bool negative = c.HasNegativeCoefficient;
        var magnitude = negative ? LinearAlgebra.Simp(-c) : c;
        var coefficient = magnitude.IsOne ? "" : magnitude is Num { Value.IsInteger: true } ? TextFormatter.Format(magnitude) : "(" + TextFormatter.Format(magnitude) + ")";
        var body = coefficient + Label(row);
        if (first) return negative ? "-" + body : body;
        return (negative ? " - " : " + ") + body;
    }

    private static string Label(int row) => Messages.T("R{0}", row + 1);

    // ---- matrix helpers ---------------------------------------------------------------------------------

    private static bool SameSize(Expr[,] a, Expr[,] b) => a.GetLength(0) == b.GetLength(0) && a.GetLength(1) == b.GetLength(1);

    private static bool IsZero(Expr e) => e.IsZero || LinearAlgebra.Simp(Expansion.Expand(e)).IsZero;

    private static bool RowEqual(Expr[,] a, Expr[,] b, int i) => RowsMatch(a, i, b, i);

    private static bool RowsMatch(Expr[,] a, int i, Expr[,] b, int k) =>
        Enumerable.Range(0, a.GetLength(1)).All(j => IsZero(a[i, j] - b[k, j]));

    private static bool Equal(Expr[,] a, Expr[,] b) => Enumerable.Range(0, a.GetLength(0)).All(i => RowEqual(a, b, i));

    private static bool RowEquivalent(Expr[,] a, Expr[,] b) => Equal(LinearAlgebra.Rref(a).Matrix, LinearAlgebra.Rref(b).Matrix);

    private static Expr[] Row(Expr[,] m, int i) => Enumerable.Range(0, m.GetLength(1)).Select(j => m[i, j]).ToArray();

    private static Expr[,] Multiply(Expr[,] t, Expr[,] m)
    {
        var r = new Expr[t.GetLength(0), m.GetLength(1)];
        for (int i = 0; i < t.GetLength(0); i++)
            for (int j = 0; j < m.GetLength(1); j++)
                r[i, j] = LinearAlgebra.Simp(Expansion.Expand(Expr.Plus(Enumerable.Range(0, m.GetLength(0)).Select(c => t[i, c] * m[c, j]))));
        return r;
    }

    /// <summary>Row echelon form: zero rows last, each leading entry right of the one above; reduced: leading 1s alone in their columns.</summary>
    private static bool IsEchelon(Expr[,] m, bool reduced)
    {
        int rows = m.GetLength(0), cols = m.GetLength(1), lastLead = -1;
        bool zeroSeen = false;
        for (int i = 0; i < rows; i++)
        {
            int lead = Enumerable.Range(0, cols).FirstOrDefault(j => !IsZero(m[i, j]), -1);
            if (lead < 0) { zeroSeen = true; continue; }
            if (zeroSeen || lead <= lastLead) return false;
            if (reduced)
            {
                if (!IsZero(m[i, lead] - Expr.One)) return false;
                for (int k = 0; k < rows; k++) if (k != i && !IsZero(m[k, lead])) return false;
            }
            lastLead = lead;
        }
        return true;
    }

    private static string Format(Expr[,] m) => TextFormatter.Format(LinearAlgebra.FromArray(m));
    private static string Format(Expr[] v) => TextFormatter.Format(Expr.List(v));
}
