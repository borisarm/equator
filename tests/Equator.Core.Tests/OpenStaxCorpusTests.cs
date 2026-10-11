using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Equator.Core.Algebra;
using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Tests;

/// <summary>
/// Textbook exercises with the book's own answers (Data/openstax.tsv, harvested from the OpenStax algebra books by
/// tools/openstax_corpus.py). Each exercise goes through the engine and the result is compared with the answer:
/// solution sets numerically (real solutions when the book's answer is real), factorisations by expansion and by
/// the number of factors, simplifications by value (variables positive, as the books assume) and by size, so a
/// result that is right but needlessly complicated fails too. Exercises the engine does not handle yet are listed
/// in Data/openstax-known.tsv with the reason; a known failure that starts passing must be removed from the list.
/// </summary>
public class OpenStaxCorpusTests
{
    public sealed record Exercise(string Book, string Section, string Id, string Kind, string Problem, string Answer)
    {
        public override string ToString() => $"{Id} {Kind}: {Problem}";
    }

    private static readonly string Data = Path.Combine(FindRoot(), "tests", "Equator.Core.Tests", "Data");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Equator.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly Lazy<List<Exercise>> All = new(() => File.ReadLines(Path.Combine(Data, "openstax.tsv"))
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split('\t'))
        .Select(f => new Exercise(f[0], f[2], f[3], f[4], f[5], f[6]))
        .ToList());

    private static readonly Lazy<Dictionary<string, string>> Known = new(() => File.ReadLines(Path.Combine(Data, "openstax-known.tsv"))
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split('\t'))
        .ToDictionary(f => f[0], f => f.Length > 1 ? f[1] : ""));

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var e in All.Value) data.Add(e.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Exercise_(string id)
    {
        var exercise = All.Value.First(e => e.Id == id);
        var failure = Evaluate(exercise);
        if (Known.Value.TryGetValue(id, out var reason))
            Assert.True(failure is not null, $"{exercise} now passes; remove it from openstax-known.tsv ({reason})");
        else
            Assert.True(failure is null, $"{exercise}\n  book: {exercise.Answer}\n  {failure}");
    }

    /// <summary>
    /// With EQUATOR_CORPUS_REPORT set to a file path, writes every failing exercise (id, reason, problem, book's
    /// answer) there: the starting point for openstax-known.tsv after the engine changes.
    /// </summary>
    [Fact]
    public void Report()
    {
        var path = Environment.GetEnvironmentVariable("EQUATOR_CORPUS_REPORT");
        if (string.IsNullOrEmpty(path)) return;
        var lines = All.Value.AsParallel().AsOrdered()
            .Select(e => (e, failure: Evaluate(e)))
            .Where(r => r.failure is not null)
            .Select(r => string.Join('\t', r.e.Id, r.e.Kind, r.failure!.Replace('\t', ' ').Replace('\n', ' '), r.e.Problem, r.e.Answer, r.e.Book, r.e.Section));
        File.WriteAllLines(path, lines);
    }

    [Fact]
    public void KnownFailuresReferToExercises()
    {
        var ids = All.Value.Select(e => e.Id).ToHashSet();
        Assert.Empty(Known.Value.Keys.Where(k => !ids.Contains(k)));
    }

    // ---- evaluation ------------------------------------------------------------------------------------

    /// <summary>Null when the engine agrees with the book, otherwise what went wrong.</summary>
    public static string? Evaluate(Exercise e)
    {
        try
        {
            return e.Kind switch
            {
                "solve" => Solve(e),
                "factor" => Factor(e),
                // Multiplying rational expressions means simplifying the product, not expanding it.
                "expand" => Simplify(e, Regex.IsMatch(e.Problem, @"/\(?[^)]*[a-z]") ? "simplify" : "expand"),
                "simplify" => Simplify(e, "simplify"),
                _ => "unknown kind",
            };
        }
        catch (Exception ex) when (ex is MathException or Parsing.ParseException or InvalidCastException or FormatException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static Expr Eval(string s) => new Kernel().EvalText(s);
    private static string Show(Expr e) => TextFormatter.Format(e);

    // ---- solving -----------------------------------------------------------------------------------------

    private static string? Solve(Exercise e)
    {
        var problem = e.Problem.Trim();
        bool system = problem.StartsWith('{');
        var equations = system ? SplitTop(problem[1..^1], ',') : [problem];
        var unknowns = Unknowns(problem);
        // Literal systems (x + y = A, x − y = B) are solved for x, y, z.
        if (system && unknowns.Count > equations.Count)
            unknowns = unknowns.Where(u => u.Name is "x" or "y" or "z").ToList();
        if (unknowns.Count == 0) return "no unknowns";
        bool inequality = !system && Regex.IsMatch(problem, "<|>");
        if (inequality) return Inequality(e, problem, unknowns[0]);
        if (unknowns.Count != equations.Count) return "skip: parameters";
        var answer = e.Answer.Trim();

        string call = system ? $"solve([{string.Join(", ", equations)}], [{string.Join(", ", unknowns)}])" : $"solve({problem}, {unknowns[0]})";
        Expr result;
        try { result = Eval(call); }
        catch (MathException ex) when (ex.Message.Contains("every value"))
        {
            return Regex.IsMatch(answer, "all real numbers|infinitely many") ? null : $"engine: identity; book: {answer}";
        }
        // Infinitely many solutions: the book writes them with a free unknown, (x, 2x - 1).
        bool dependent = Regex.IsMatch(answer, "all real numbers|infinitely many")
            || system && unknowns.Any(u => Regex.IsMatch(answer, $@"(?<![A-Za-z]){u.Name}(?![A-Za-z(])"));
        if (dependent)
            return result is ListExpr { Items.Count: > 0 } l && l.Items.All(i => i is ListExpr li && li.Items.Count < unknowns.Count)
                ? null : $"engine: {Show(result)}; book: {answer}";

        var expected = AnswerPoints(answer, unknowns);
        var got = ResultPoints(result, unknowns);
        if (got is null) return $"engine result not explicit: {Show(result)}";
        // With a real answer in the book, compare real solutions only (complex roots are an extension).
        bool realBook = expected.All(p => p.All(v => Math.Abs(v.Imaginary) < 1e-9));
        if (realBook) got = got.Where(p => p.All(v => Math.Abs(v.Imaginary) < 1e-9)).ToList();
        if (SameSets(expected, got)) return null;
        // Trigonometric equations: the books list the solutions in [0, 2π), solve gives a principal period.
        if (!system && realBook && Regex.IsMatch(problem, @"\b(sin|cos|tan)\(") && SameSets(Mod2Pi(expected), Mod2Pi(got))) return null;
        return $"engine: {Show(result)}";
    }

    /// <summary>The book's solutions: "x = 2, x = -3", "a = ±7", "(2, 3), (-1, 4)", "no solution", bare values.</summary>
    private static List<Complex[]> AnswerPoints(string answer, List<Sym> unknowns)
    {
        if (Regex.IsMatch(answer, "^no solution$")) return [];
        var points = new List<Complex[]>();
        // "x = 0 x = 4" written without a separator reads as 0*x = 4.
        answer = Regex.Replace(answer, @"(\d)\*([a-z]) = ", "$1, $2 = ");
        var pieces = SplitTop(Regex.Replace(answer, @"\s+(or|and)\s+", ", "), ',')
            .Where(p => !p.Contains("!="))                       // restrictions such as x != 0
            .Select(p => p.Count(c => c == '=') > 1 ? p[..p.IndexOf('=')] + "=" + p[(p.LastIndexOf('=') + 1)..] : p) // x = 2^(-3) = 1/8
            .ToList();
        if (unknowns.Count > 1)
        {
            // Ordered pairs/triples (2, 3), or "x = 2, y = 3".
            if (pieces.All(p => p.Contains('=')))
            {
                var map = pieces.Select(p => p.Split('=')).ToDictionary(p => p[0].Trim(), p => Value(Eval(p[1])));
                return [unknowns.Select(u => map[u.Name]).ToArray()];
            }
            foreach (var p in pieces)
            {
                // (2, 3), or a common factor taken out: 1/20*(10, 5, 4).
                var m = Regex.Match(p.Trim(), @"^(?:(.+)\*)?(\(.*\))$");
                if (!m.Success) throw new FormatException("expected an ordered pair: " + p);
                var scale = m.Groups[1].Success ? Value(Eval(m.Groups[1].Value)) : Complex.One;
                var inner = m.Groups[2].Value;
                points.Add(SplitTop(inner[1..^1], ',').Select(v => scale * Value(Eval(v))).ToArray());
            }
            return points;
        }
        foreach (var piece in pieces)
        {
            var value = piece.Contains('=') ? piece[(piece.IndexOf('=') + 1)..] : piece;
            foreach (var v in ExpandPlusMinus(value))
                points.Add([Value(Eval(v))]);
        }
        return points;
    }

    /// <summary>The unknowns of a problem as written (evaluating it could turn an always-true inequality into True).</summary>
    private static List<Sym> Unknowns(string problem) =>
        Regex.Matches(problem, @"(?<![A-Za-z])(theta|alpha|beta|[A-Za-z])(?![A-Za-z(])")
            .Select(m => m.Value).Where(n => n is not ("e" or "I")).Distinct()
            .OrderBy(n => n, StringComparer.Ordinal).Select(n => new Sym(n)).ToList();

    /// <summary>Sample values for the parameters of literal equations (x + y = A, x − y = B).</summary>
    private static readonly Dictionary<string, Complex> Parameters = new()
    {
        ["A"] = 1.7, ["B"] = -0.6, ["C"] = 2.3, ["D"] = 0.9, ["E"] = -1.4, ["F"] = 3.1,
    };

    private static Complex Value(Expr e) => NumericEval.Eval(e, Parameters);

    private static IEnumerable<string> ExpandPlusMinus(string s) =>
        s.Contains('±') ? [s.Replace("±", "+"), s.Replace("±", "-")] : s.Contains('∓') ? [s.Replace("∓", "-"), s.Replace("∓", "+")] : [s];

    /// <summary>Engine solutions [x = 2, x = 3] or [[x = 1, y = 2], ...] as points; null when not explicit.</summary>
    private static List<Complex[]>? ResultPoints(Expr result, List<Sym> unknowns)
    {
        if (result is not ListExpr list) return null;
        var points = new List<Complex[]>();
        foreach (var item in list.Items)
        {
            var eqs = item is ListExpr inner ? inner.Items : [item];
            var values = new Dictionary<string, Complex>();
            foreach (var eq in eqs)
            {
                if (eq is not EqExpr { Lhs: Sym s } q || unknowns.Any(q.Rhs.Contains)) return null;
                values[s.Name] = Value(q.Rhs);
            }
            if (unknowns.Any(u => !values.ContainsKey(u.Name))) return null;
            points.Add(unknowns.Select(u => values[u.Name]).ToArray());
        }
        return points;
    }

    private static List<Complex[]> Mod2Pi(List<Complex[]> points) =>
        points.Select(p => p.Select(v => (Complex)(((v.Real % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI))).ToArray()).ToList();

    private static bool SameSets(List<Complex[]> a, List<Complex[]> b)
    {
        static bool Close(Complex[] p, Complex[] q) => p.Zip(q).All(t => Complex.Abs(t.First - t.Second) < 1e-6 * (1 + Complex.Abs(t.First)));
        return a.All(p => b.Any(q => Close(p, q))) && b.All(q => a.Any(p => Close(p, q)));
    }

    private static string? Inequality(Exercise e, string problem, Sym x)
    {
        var result = Eval($"solve({problem}, {x})");
        if (result is not ListExpr list) return $"engine: {Show(result)}";
        Func<double, bool> engine = t => list.Items.Any(rel => Holds(rel, x, t));
        Func<double, bool> book = BookSet(e.Answer.Trim(), x);
        foreach (var t in SamplePoints(e.Answer + " " + Show(result)))
            if (engine(t) != book(t)) return $"engine: {Show(result)} (differs at {x} = {t.ToString(CultureInfo.InvariantCulture)})";
        return null;
    }

    private static bool Holds(Expr rel, Sym x, double t)
    {
        foreach (var (op, f) in Inequalities.Conditions(rel))
        {
            var v = NumericEval.Eval(f, new Dictionary<string, Complex> { [x.Name] = t }).Real;
            bool ok = op switch { "<" => v < -1e-12, ">" => v > 1e-12, "<=" => v <= 1e-12, ">=" => v >= -1e-12, _ => Math.Abs(v) <= 1e-12 };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The book's answer as a set: interval notation with unions, a relation, "all real numbers" or "no solution".</summary>
    private static Func<double, bool> BookSet(string answer, Sym x)
    {
        if (answer.Contains("all real numbers") || answer == "(-oo, oo)") return _ => true;
        if (answer == "no solution") return _ => false;
        if (Regex.IsMatch(answer, "^[\\(\\[]"))
        {
            var parts = answer.Split(" U ").Select(p => p.Trim()).ToList();
            var tests = parts.Select(p =>
            {
                bool lo = p[0] == '[', hi = p[^1] == ']';
                var ends = SplitTop(p[1..^1], ',');
                double a = Bound(ends[0]), b = Bound(ends[1]);
                return (Func<double, bool>)(t => (lo ? t >= a - 1e-12 : t > a + 1e-12) && (hi ? t <= b + 1e-12 : t < b - 1e-12));
            }).ToList();
            return t => tests.Any(f => f(t));
        }
        var rel = Eval(answer.Replace(" or ", ", ").Contains(", ") ? throw new FormatException("relation list") : answer);
        return t => Holds(rel, x, t);
    }

    private static double Bound(string s) => s.Trim() switch { "oo" => double.PositiveInfinity, "-oo" => double.NegativeInfinity, var v => NumericEval.EvalReal(Eval(v)) };

    /// <summary>Points that separate the sets: around every number in the answers, plus a grid.</summary>
    private static IEnumerable<double> SamplePoints(string texts)
    {
        var points = new List<double>();
        for (double t = -30.05; t <= 30; t += 0.25) points.Add(t);
        foreach (Match m in Regex.Matches(texts, @"-?\d+(\.\d+)?(/\d+)?"))
        {
            double v = m.Value.Contains('/') ? double.Parse(m.Value.Split('/')[0], CultureInfo.InvariantCulture) / double.Parse(m.Value.Split('/')[1], CultureInfo.InvariantCulture)
                : double.Parse(m.Value, CultureInfo.InvariantCulture);
            points.AddRange([v, v - 1e-4, v + 1e-4]);
        }
        return points;
    }

    // ---- factoring -------------------------------------------------------------------------------------

    private static string? Factor(Exercise e)
    {
        var problem = Eval(e.Problem);
        var result = Eval($"factor({e.Problem})");
        if (!Equivalent(result, problem)) return $"engine factorisation is not equal to the problem: {Show(result)}";
        int got = FactorCount(result);
        if (e.Answer == "prime") return got <= 1 ? null : $"engine: {Show(result)}; book: prime";
        var answer = Eval(e.Answer);
        if (!Equivalent(answer, problem)) return "skip: book answer does not match the problem";
        int expected = FactorCount(answer);
        // At least as far as the book (exercises on the greatest common factor stop early on purpose).
        return got >= expected ? null : $"engine: {Show(result)} ({got} factors, book {expected})";
    }

    /// <summary>Non-constant factors with multiplicity: 3x(x - 1)²(x + 2) has 4.</summary>
    private static int FactorCount(Expr e)
    {
        var factors = e is MulExpr m ? m.Factors : [e];
        int count = 0;
        foreach (var f in factors)
        {
            if (f.IsConstant) continue;
            if (f is PowExpr { Exponent: Num { Value.IsInteger: true } n } && !((PowExpr)f).Base.IsConstant) count += (int)n.Value.Num;
            else if (f is Sym) count += 1;
            else count += 1;
        }
        return count;
    }

    // ---- simplification ----------------------------------------------------------------------------------

    private static string? Simplify(Exercise e, string command)
    {
        var problem = Eval(e.Problem);
        var symbols = problem.FreeSymbols().Select(s => s.Name).ToList();
        var conditions = string.Concat(symbols.Select(s => $", {s} > 0"));
        var result = Eval($"{command}({e.Problem}{(command == "simplify" ? conditions : "")})");
        Expr answer;
        try { answer = Eval(e.Answer); }
        catch (Exception ex) when (ex is MathException or Parsing.ParseException) { return "skip: book answer unreadable"; }
        if (!Equivalent(answer, problem)) return "skip: book answer does not match the problem";
        if (!Equivalent(result, problem)) return $"engine result is not equal to the problem: {Show(result)}";
        // The engine's form may differ from the book's, but it should not be clearly bigger.
        int size = result.Complexity(), bookSize = answer.Complexity();
        return size <= bookSize + 2 + bookSize / 4 ? null : $"engine: {Show(result)} (size {size}, book {bookSize})";
    }

    private static readonly double[] Samples = [0.37, 1.29, 0.83, 2.17, 1.61, 0.71, 3.07, 2.39];

    /// <summary>Equal at several positive sample points (the books take variables positive).</summary>
    private static bool Equivalent(Expr a, Expr b)
    {
        var syms = a.FreeSymbols().Union(b.FreeSymbols()).Select(s => s.Name).Distinct().ToList();
        int compared = 0;
        for (int trial = 0; trial < Samples.Length; trial++)
        {
            var vars = new Dictionary<string, Complex>();
            for (int j = 0; j < syms.Count; j++) vars[syms[j]] = Samples[(trial + 3 * j) % Samples.Length];
            Complex va, vb;
            try { va = NumericEval.Eval(a, vars); vb = NumericEval.Eval(b, vars); }
            catch (MathException) { continue; }
            if (!double.IsFinite(va.Real) || !double.IsFinite(vb.Real)) continue;
            compared++;
            if (Complex.Abs(va - vb) > 1e-7 * (1 + Complex.Abs(va))) return false;
        }
        return compared > 0;
    }

    private static List<string> SplitTop(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == separator && depth == 0) { parts.Add(s[start..i].Trim()); start = i + 1; }
        }
        parts.Add(s[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }
}
